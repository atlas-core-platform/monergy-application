using System.Globalization;
using System.Security.Cryptography;
using Monergy.Contracts;

namespace Monergy.Services.CustomerIdentity.Application;

public sealed record RegistrationIdentity(string TenantId, string Email, string ActorId, string CustomerId,
    string PasswordHash, long CredentialVersion, bool Enrolled);
public sealed record RegistrationCode(string Purpose, string Email, string ChallengeHash, string CodeHash,
    byte[] ProtectedCode, DateTimeOffset RequestedAt);
public sealed record RegistrationCompletion(string TenantId, string ActorId, string CustomerId, bool Active);
public interface ICustomerRegistrationRepository
{
    Task QueueAsync(string tenant, RegistrationCode code, CancellationToken ct);
    Task<RegistrationIdentity?> RegisterAsync(string tenant, string challenge, string verifier,
        string name, string passwordHash, AccountAgreementPublication publication, CancellationToken ct);
    Task<RegistrationIdentity?> ReadForSignInAsync(string tenant, string email, CancellationToken ct);
    Task<bool> RecordSignInAsync(RegistrationIdentity identity, bool valid, CancellationToken ct);
    Task<bool> CurrentAsync(RegistrationIdentity identity, CancellationToken ct);
    Task MarkEnrolledAsync(RegistrationIdentity identity, CancellationToken ct);
    Task<bool> RecoverAsync(string tenant, string challenge, string verifier, string passwordHash, CancellationToken ct);
}
public interface ICustomerEnrollmentClient
{
    Task EnrollAsync(string tenant, string actor, string email, CancellationToken ct);
}

public sealed class CustomerRegistration(ICustomerRegistrationRepository repository, CustomerEmailCodeProtector protector,
    CustomerPasswordPolicy passwords, AccountAgreementCatalog agreements, ICustomerEnrollmentClient enrollment,
    TenantSessionAuthority sessions, TimeProvider clock)
{
    public AccountAgreementPublication Publication(string tenant)
    {
        TenantAccessProtocol.Identifier(tenant,64);
        return agreements.Read(tenant,clock.GetUtcNow()) ?? throw new TenantAccessException("AGREEMENTS_NOT_PUBLISHED",409);
    }
    public async Task<CustomerEmailChallenge> RequestAsync(RegistrationRequest request, bool recovery, CancellationToken ct)
    {
        TenantAccessProtocol.Identifier(request.TenantId,64);
        if(!recovery) _=Publication(request.TenantId);
        var email=CustomerEmailAccess.NormalizeEmail(request.Email);
        var purpose=recovery?"recovery":"registration";
        var id=Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var code=RandomNumberGenerator.GetInt32(1_000_000).ToString("D6",CultureInfo.InvariantCulture);
        var hash=CustomerEmailCodeProtector.ChallengeHash(id);
        var now=clock.GetUtcNow();
        await repository.QueueAsync(request.TenantId,new(purpose,email,hash,
            protector.Verifier(request.TenantId+":"+purpose,id,code),
            protector.Protect(request.TenantId+":"+purpose,hash,code),now),ct);
        return new(id,now.AddMinutes(5),60);
    }
    public async Task<RegistrationCompletion> ConfirmAsync(RegistrationConfirmation request,CancellationToken ct)
    {
        CustomerRegistrationPolicy.Validate(request);
        var publication=Publication(request.TenantId);
        if(publication.Revision!=request.AgreementRevision)throw new TenantAccessException("AGREEMENTS_PUBLICATION_CHANGED",409);
        var identity=await repository.RegisterAsync(request.TenantId,CustomerEmailCodeProtector.ChallengeHash(request.ChallengeId),
            protector.Verifier(request.TenantId+":registration",request.ChallengeId,request.Code),request.DisplayName,
            passwords.Hash(request.Password),publication,ct)??throw new TenantAccessException("EMAIL_CODE_INVALID",401);
        await Enroll(identity,ct);
        return new(identity.TenantId,identity.ActorId,identity.CustomerId,true);
    }
    public async Task<TenantSessionContext> SignInAsync(PasswordSignIn request,CancellationToken ct)
    {
        TenantAccessProtocol.Identifier(request.TenantId,64);
        var email=CustomerEmailAccess.NormalizeEmail(request.Email);
        var identity=await repository.ReadForSignInAsync(request.TenantId,email,ct);
        var valid=passwords.Verify(identity?.PasswordHash,request.Password);
        if(identity is null||!await repository.RecordSignInAsync(identity,valid,ct)||!valid)
            throw new TenantAccessException("AUTHENTICATION_REQUIRED",401);
        // A lost enrollment receipt is resumed with verified credentials, never
        // by replaying an OTP or replacing an existing imported principal.
        if(!identity.Enrolled)await Enroll(identity,ct);
        var session=await sessions.EstablishVerifiedAsync(identity.TenantId,identity.ActorId,ct);
        try
        {
            if(!await repository.CurrentAsync(identity,ct))throw new TenantAccessException("AUTHENTICATION_REQUIRED",401);
            return session;
        }
        catch
        {
            await sessions.EndAsync(new(identity.TenantId,session.AuthenticationContextId),CancellationToken.None);
            throw;
        }
    }
    public async Task RecoverAsync(PasswordRecoveryConfirmation request,CancellationToken ct)
    {
        TenantAccessProtocol.Identifier(request.TenantId,64);
        CustomerRegistrationPolicy.ValidateChallenge(request.ChallengeId,request.Code);
        var hash=passwords.Hash(request.NewPassword);
        if(!await repository.RecoverAsync(request.TenantId,CustomerEmailCodeProtector.ChallengeHash(request.ChallengeId),
            protector.Verifier(request.TenantId+":recovery",request.ChallengeId,request.Code),hash,ct))
            throw new TenantAccessException("EMAIL_CODE_INVALID",401);
    }
    private async Task Enroll(RegistrationIdentity identity,CancellationToken ct)
    {
        await enrollment.EnrollAsync(identity.TenantId,identity.ActorId,identity.Email,ct);
        if(!await repository.CurrentAsync(identity,ct))throw new TenantAccessException("REGISTRATION_CHANGED",409);
        await repository.MarkEnrolledAsync(identity,ct);
    }
}
