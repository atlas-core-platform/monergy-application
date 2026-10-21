using System.Text.Json;
using Dapper;
namespace Monergy.AccessManagement;

public sealed record CustomerEnrollmentRequest(string ActorId,string Email);
public static class CustomerEnrollment
{
    private static readonly string[] EventTypes = ["AuthorizationPolicyChanged", "AccessManagementAuditEvidence"];
    public static void MapCustomerEnrollment(this WebApplication app,string token)
    {
        ReferenceOwnerBoundary.ValidateToken(token);
        if(token==app.Configuration["Monergy:AccessManagement:MembershipToken"]||token==app.Configuration["Monergy:AccessManagement:CustomerIdentityToken"]||token==app.Configuration["Monergy:AccessManagement:IdentityProvisioningToken"]||token==app.Configuration["Monergy:AccessManagement:CustomerContextToken"]||token==app.Configuration["Monergy:AccessManagement:ConsentToken"])
            throw new InvalidOperationException("Enrollment requires a dedicated owner credential.");
        app.MapPost("/internal/v1/customer-enrollments",async(CustomerEnrollmentRequest request,HttpContext http,ITenantConnectionFactory connections)=>
        {
            if(!ReferenceOwnerBoundary.TokenMatches(http.Request.Headers["X-Monergy-Owner-Token"].ToString(),token))return (IResult)Results.StatusCode(403);
            var tenant=http.Request.Headers["X-Monergy-Tenant"].ToString();
            try
            {
                AccessInput.Identifier(tenant,64);AccessInput.Identifier(request.ActorId);
                AccessInput.Require(request.ActorId.StartsWith("ci-",StringComparison.Ordinal)&&!string.IsNullOrWhiteSpace(request.Email)&&request.Email==request.Email.Trim()&&!request.Email.Any(char.IsUpper)&&request.Email.Length is >=3 and <=320,"ENROLLMENT_INVALID",400);
                await using var db=await connections.OpenAsync(tenant,http.RequestAborted);await using var tx=await db.BeginTransactionAsync(http.RequestAborted);
                await db.ExecuteScalarAsync<long>(new CommandDefinition("SELECT policy_version FROM am.policy_state WHERE singleton FOR UPDATE;",transaction:tx,cancellationToken:http.RequestAborted));
                var prior=await db.QuerySingleOrDefaultAsync<Member>(new CommandDefinition("SELECT actor_id AS Actor,normalized_email AS Email,status AS Status,business_role_id AS Role,administrative_authority AS Authority FROM am.access_subjects WHERE actor_id=@ActorId OR normalized_email=@Email;",request,tx,cancellationToken:http.RequestAborted));
                if(prior is not null)
                {
                    // A retry never restores a disabled or changed membership.
                    AccessInput.Require(prior.Actor==request.ActorId&&prior.Email==request.Email&&prior.Status=="Active"&&prior.Role=="default-role-customer"&&prior.Authority is null,"ENROLLMENT_CONFLICT",409);
                }
                else
                {
                    AccessInput.Require(!await db.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT EXISTS(SELECT 1 FROM am.import_email_reservations WHERE normalized_email=@Email);",request,tx,cancellationToken:http.RequestAborted)),"ENROLLMENT_CONFLICT",409);
                    AccessInput.Require(await db.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT EXISTS(SELECT 1 FROM am.roles WHERE role_id='default-role-customer' AND code='customer');",transaction:tx,cancellationToken:http.RequestAborted)),"CUSTOMER_ROLE_UNAVAILABLE",503);
                    await db.ExecuteAsync(new CommandDefinition("INSERT INTO am.access_subjects(actor_id,normalized_email,status,business_role_id) VALUES(@ActorId,@Email,'Active','default-role-customer');",request,tx,cancellationToken:http.RequestAborted));
                    var version=await db.ExecuteScalarAsync<long>(new CommandDefinition("UPDATE am.policy_state SET policy_version=policy_version+1 WHERE singleton RETURNING policy_version;",transaction:tx,cancellationToken:http.RequestAborted));
                    foreach(var type in EventTypes)
                        await db.ExecuteAsync(new CommandDefinition("""
                        INSERT INTO am.outbox(event_id,event_type,tenant_id,aggregate_id,initiator_actor_id,correlation_id,policy_version,subject_version,payload)
                        VALUES(@Id,@Type,@Tenant,@Aggregate,@Actor,@Correlation,@Version,1,CAST(@Payload AS jsonb));
                        """,new{Id=Guid.NewGuid(),Type=type,Tenant=tenant,Aggregate=type=="AuthorizationPolicyChanged"?tenant:request.ActorId,Actor=request.ActorId,Correlation="registration-"+request.ActorId,Version=version,Payload=JsonSerializer.Serialize(new{operation="customer.enrolled",subjectActorId=request.ActorId})},tx,cancellationToken:http.RequestAborted));
                }
                await tx.CommitAsync(http.RequestAborted);
                return Results.Ok(new{tenantId=tenant,actorId=request.ActorId,email=request.Email});
            }
            catch(AccessAdministrationException e){return Results.Json(new{error=e.Code},statusCode:e.StatusCode);}
            catch(Exception e)when(e is AccessStoreUnavailableException or System.Data.Common.DbException or InvalidOperationException){return Results.StatusCode(503);}
        });
    }
    private sealed record Member(string Actor,string Email,string Status,string? Role,string? Authority);
}
