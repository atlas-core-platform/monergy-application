using System.Text.Json;
using Dapper;
using Monergy.Contracts;
using Monergy.Services.CustomerIdentity.Application;
using Npgsql;

namespace Monergy.Services.CustomerIdentity.Infrastructure;

public sealed class PostgresCustomerRegistration(PostgresTenantSessions sessions):ICustomerRegistrationRepository
{
    public async Task QueueAsync(string tenant,RegistrationCode code,CancellationToken ct)
    {
        await using var db=await sessions.OpenAsync(tenant,ct);
        await db.ExecuteAsync(new CommandDefinition("""
        INSERT INTO customer_identity.registration_challenges AS c
        (purpose,email,challenge_hash,code_hash,protected_code,requested_at,expires_at,window_at,requests)
        SELECT @Purpose,@Email,@ChallengeHash,@CodeHash,@ProtectedCode,@RequestedAt,@RequestedAt+interval '5 minutes',@RequestedAt,1
        WHERE (@Purpose='registration' AND NOT EXISTS(SELECT 1 FROM customer_identity.tenant_principals WHERE normalized_email=@Email))
          OR (@Purpose='recovery' AND EXISTS(SELECT 1 FROM customer_identity.registered_credentials r
              JOIN customer_identity.tenant_principals p USING(actor_id) WHERE p.normalized_email=@Email))
        ON CONFLICT(purpose,email) DO UPDATE SET challenge_hash=EXCLUDED.challenge_hash,code_hash=EXCLUDED.code_hash,
          protected_code=EXCLUDED.protected_code,requested_at=EXCLUDED.requested_at,expires_at=EXCLUDED.expires_at,
          window_at=CASE WHEN c.window_at<=@RequestedAt-interval '1 hour' THEN @RequestedAt ELSE c.window_at END,
          requests=CASE WHEN c.window_at<=@RequestedAt-interval '1 hour' THEN 1 ELSE c.requests+1 END,
          attempts=0,consumed=false,delivered=false,delivery_attempt=0,lease_until=NULL
        WHERE c.requested_at<=@RequestedAt-interval '1 minute' AND (c.window_at<=@RequestedAt-interval '1 hour' OR c.requests<5);
        """,code,cancellationToken:ct));
    }
    private static async Task<string?> Consume(NpgsqlConnection db,NpgsqlTransaction tx,string purpose,string hash,string verifier,CancellationToken ct)
    {
        // Failed attempts are committed by callers too; rolling back a rejected
        // verifier would otherwise permit unlimited guesses.
        var row=await db.QuerySingleOrDefaultAsync<Challenge>(new CommandDefinition("""
        UPDATE customer_identity.registration_challenges SET attempts=attempts+1,
          consumed=(code_hash=@Verifier),protected_code=NULL
        WHERE purpose=@Purpose AND challenge_hash=@Hash AND delivered AND NOT consumed AND attempts<5
          AND expires_at>clock_timestamp()
        RETURNING email AS Email,code_hash=@Verifier AS Valid;
        """,new{Purpose=purpose,Hash=hash,Verifier=verifier},tx,cancellationToken:ct));
        return row?.Valid==true?row.Email:null;
    }
    public async Task<RegistrationIdentity?> RegisterAsync(string tenant,string challenge,string verifier,string name,string passwordHash,AccountAgreementPublication publication,CancellationToken ct)
    {
        await using var db=await sessions.OpenAsync(tenant,ct);await using var tx=await db.BeginTransactionAsync(ct);
        // Shared with imported identity creation: neither path can claim the other's email.
        await db.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(705006);",transaction:tx,cancellationToken:ct));
        var email=await Consume(db,tx,"registration",challenge,verifier,ct);
        if(email is null){await tx.CommitAsync(ct);return null;}
        if(await db.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT EXISTS(SELECT 1 FROM customer_identity.tenant_principals WHERE normalized_email=@Email);",new{Email=email},tx,cancellationToken:ct)))
        {await tx.CommitAsync(ct);return null;}
        var actor="ci-"+Guid.NewGuid().ToString("N");var customer="customer-"+Guid.NewGuid().ToString("N");
        var snapshot=JsonSerializer.Serialize(publication);
        await db.ExecuteAsync(new CommandDefinition("""
        INSERT INTO customer_identity.tenant_principals(actor_id,normalized_email) VALUES(@Actor,@Email);
        INSERT INTO customer_identity.customers(customer_id,display_name,owner_actor_id,ownership_evidence)
          VALUES(@Customer,@Name,@Actor,'Verified email registration');
        INSERT INTO customer_identity.customer_profiles(customer_id,display_name,version,updated_at) VALUES(@Customer,@Name,1,clock_timestamp());
        INSERT INTO customer_identity.registered_credentials(actor_id,customer_id,password_hash) VALUES(@Actor,@Customer,@PasswordHash);
        INSERT INTO customer_identity.account_agreement_publications(revision,content_json,published_at)
          VALUES(@Revision,@Snapshot,@PublishedAt) ON CONFLICT DO NOTHING;
        """,new{Actor=actor,Email=email,Customer=customer,Name=name,PasswordHash=passwordHash,publication.Revision,Snapshot=snapshot,publication.PublishedAt},tx,cancellationToken:ct));
        var saved=await db.QuerySingleAsync<string>(new CommandDefinition("SELECT content_json FROM customer_identity.account_agreement_publications WHERE revision=@Revision;",new{publication.Revision},tx,cancellationToken:ct));
        if(saved!=snapshot)throw new TenantAccessException("AGREEMENTS_PUBLICATION_INVALID",503);
        await db.ExecuteAsync(new CommandDefinition("""
        INSERT INTO customer_identity.account_agreement_acceptances(operation_id,customer_id,actor_id,revision,terms_accepted,privacy_acknowledged,processing_consented,accepted_at)
          VALUES(@Operation,@Customer,@Actor,@Revision,true,true,true,clock_timestamp());
        INSERT INTO customer_identity.registration_events(event_id,action,actor_id,customer_id) VALUES(@Event,'REGISTERED_PENDING_ENROLLMENT',@Actor,@Customer);
        INSERT INTO customer_identity.email_access_events(event_id,challenge_hash,actor_id,customer_id,outcome,recorded_at)
          VALUES(@VerifiedEvent,@Challenge,@Actor,@Customer,'CODE_VERIFIED',clock_timestamp());
        """,new{Operation=Guid.NewGuid(),Event=Guid.NewGuid(),VerifiedEvent=Guid.NewGuid(),Challenge=challenge,Actor=actor,Customer=customer,publication.Revision},tx,cancellationToken:ct));
        await tx.CommitAsync(ct);return new(tenant,email,actor,customer,passwordHash,1,false);
    }
    public async Task<RegistrationIdentity?> ReadForSignInAsync(string tenant,string email,CancellationToken ct)
    {
        await using var db=await sessions.OpenAsync(tenant,ct);
        return await db.QuerySingleOrDefaultAsync<RegistrationIdentity>(new CommandDefinition("""
        SELECT @Tenant AS TenantId,p.normalized_email AS Email,r.actor_id AS ActorId,r.customer_id AS CustomerId,
          r.password_hash AS PasswordHash,r.credential_version AS CredentialVersion,r.enrolled AS Enrolled
        FROM customer_identity.registered_credentials r JOIN customer_identity.tenant_principals p USING(actor_id)
        JOIN customer_identity.current_customers c ON c.customer_id=r.customer_id AND c.owner_actor_id=r.actor_id
        WHERE p.normalized_email=@Email AND c.active;
        """,new{Tenant=tenant,Email=email},cancellationToken:ct));
    }
    public async Task<bool> RecordSignInAsync(RegistrationIdentity identity,bool valid,CancellationToken ct)
    {
        await using var db=await sessions.OpenAsync(identity.TenantId,ct);await using var tx=await db.BeginTransactionAsync(ct);
        var updated=await db.ExecuteAsync(new CommandDefinition("""
        UPDATE customer_identity.registered_credentials SET
          failed_attempts=CASE WHEN @Valid THEN 0 WHEN locked_until<=clock_timestamp() THEN 1 ELSE failed_attempts+1 END,
          locked_until=CASE WHEN @Valid THEN NULL WHEN failed_attempts>=4 AND (locked_until IS NULL OR locked_until>clock_timestamp()) THEN clock_timestamp()+interval '15 minutes' ELSE NULL END
        WHERE actor_id=@ActorId AND credential_version=@CredentialVersion AND (locked_until IS NULL OR locked_until<=clock_timestamp());
        """,new{identity.ActorId,identity.CredentialVersion,Valid=valid},tx,cancellationToken:ct));
        await db.ExecuteAsync(new CommandDefinition("INSERT INTO customer_identity.registration_events(event_id,action,actor_id,customer_id) VALUES(@Event,@Action,@ActorId,@CustomerId);",
            new{Event=Guid.NewGuid(),Action=valid&&updated==1?"PASSWORD_VERIFIED":"PASSWORD_REJECTED",identity.ActorId,identity.CustomerId},tx,cancellationToken:ct));
        await tx.CommitAsync(ct);return updated==1;
    }
    public async Task<bool> CurrentAsync(RegistrationIdentity identity,CancellationToken ct)
    {
        await using var db=await sessions.OpenAsync(identity.TenantId,ct);
        return await db.ExecuteScalarAsync<bool>(new CommandDefinition("""
        SELECT EXISTS(SELECT 1 FROM customer_identity.registered_credentials r JOIN customer_identity.current_customers c
         ON c.customer_id=r.customer_id AND c.owner_actor_id=r.actor_id WHERE r.actor_id=@ActorId
         AND r.customer_id=@CustomerId AND r.credential_version=@CredentialVersion AND c.active);
        """,identity,cancellationToken:ct));
    }
    public async Task MarkEnrolledAsync(RegistrationIdentity identity,CancellationToken ct)
    {
        await using var db=await sessions.OpenAsync(identity.TenantId,ct);
        if(await db.ExecuteAsync(new CommandDefinition("UPDATE customer_identity.registered_credentials SET enrolled=true WHERE actor_id=@ActorId AND credential_version=@CredentialVersion;",identity,cancellationToken:ct))!=1)
            throw new TenantAccessException("REGISTRATION_CHANGED",409);
    }
    public async Task<bool> RecoverAsync(string tenant,string challenge,string verifier,string passwordHash,CancellationToken ct)
    {
        await using var db=await sessions.OpenAsync(tenant,ct);await using var tx=await db.BeginTransactionAsync(ct);
        var email=await Consume(db,tx,"recovery",challenge,verifier,ct);
        if(email is null){await tx.CommitAsync(ct);return false;}
        var actor=await db.QuerySingleOrDefaultAsync<string>(new CommandDefinition("""
        UPDATE customer_identity.registered_credentials r SET password_hash=@PasswordHash,credential_version=credential_version+1,
         failed_attempts=0,locked_until=NULL FROM customer_identity.tenant_principals p
        WHERE p.actor_id=r.actor_id AND p.normalized_email=@Email RETURNING r.actor_id;
        """,new{Email=email,PasswordHash=passwordHash},tx,cancellationToken:ct));
        if(actor is null){await tx.CommitAsync(ct);return false;}
        await db.ExecuteAsync(new CommandDefinition("""
        UPDATE customer_identity.trusted_sessions SET revoked=true WHERE actor_id=@Actor;
        UPDATE customer_identity.email_access_challenges SET consumed=true,protected_code=NULL WHERE actor_id=@Actor;
        INSERT INTO customer_identity.registration_events(event_id,action,actor_id) VALUES(@Event,'PASSWORD_RECOVERED',@Actor);
        """,new{Actor=actor,Event=Guid.NewGuid()},tx,cancellationToken:ct));await tx.CommitAsync(ct);return true;
    }
    private sealed record Challenge(string Email,bool Valid);
}
