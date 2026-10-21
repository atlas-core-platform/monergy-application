using System.Security.Cryptography;
using Dapper;
using Microsoft.Extensions.Configuration;
using Monergy.Services.CustomerIdentity.Application;
using Monergy.Services.CustomerIdentity.Infrastructure;
using Npgsql;
using Xunit;
namespace Monergy.AccessIntegration.Tests;
// Requires the current LOCAL/CI bootstrap, including migration 0011 and runtime grants.
[Trait("Category","Physical")]
[Collection("CustomerIdentityPhysicalOwners")]
public sealed class PostgresRegistrationTests
{
    [Fact]
    public async Task RegistrationIsSingleUseTenantBoundAndRecoveryRevokesSessions()
    {
        var settings=new Dictionary<string,string?>
        {
            ["Monergy:ExecutionZone"]="CI_EPHEMERAL",["Monergy:AccessIntegration:Enabled"]="true",
            ["Monergy:AccessIntegration:CustomerIdentityDatabases:T001"]=Required("AM05_CI_T001_RUNTIME"),
            ["Monergy:AccessIntegration:CustomerIdentityDatabases:T002"]=Required("AM05_CI_T002_RUNTIME")
        };
        await using var sessions=new PostgresTenantSessions(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        var repo=new PostgresCustomerRegistration(sessions);
        await using var db=new NpgsqlConnection(Required("AM05_CI_T001_OWNER"));await db.OpenAsync();
        var email=Guid.NewGuid().ToString("N")+"@example.test";
        var hash=Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));var verifier=new string('b',64);
        var publication=new AccountAgreementPublication(hash,DateTimeOffset.UtcNow,[]);
        await repo.QueueAsync("T001",new("registration",email,hash,verifier,new byte[34],DateTimeOffset.UtcNow),default);
        await db.ExecuteAsync("UPDATE customer_identity.registration_challenges SET delivered=true,protected_code=NULL WHERE challenge_hash=@Hash;",new{Hash=hash});
        Assert.Null(await repo.RegisterAsync("T002",hash,verifier,"Test customer","test-hash",publication,default));
        Assert.Null(await repo.RegisterAsync("T001",hash,"wrong","Test customer","test-hash",publication,default));
        Assert.Equal(1,await db.ExecuteScalarAsync<int>("SELECT attempts FROM customer_identity.registration_challenges WHERE challenge_hash=@Hash;",new{Hash=hash}));
        var identity=await repo.RegisterAsync("T001",hash,verifier,"Test customer","test-hash",publication,default);Assert.NotNull(identity);
        Assert.Null(await repo.RegisterAsync("T001",hash,verifier,"Test customer","test-hash",publication,default));
        await sessions.CreateAsync("T001",identity.ActorId,"registration-session-"+hash,1,default);
        var recovery=Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        await repo.QueueAsync("T001",new("recovery",email,recovery,verifier,new byte[34],DateTimeOffset.UtcNow),default);
        await db.ExecuteAsync("UPDATE customer_identity.registration_challenges SET delivered=true,protected_code=NULL WHERE challenge_hash=@Hash;",new{Hash=recovery});
        Assert.False(await repo.RecoverAsync("T002",recovery,verifier,"new-hash",default));
        Assert.True(await repo.RecoverAsync("T001",recovery,verifier,"new-hash",default));
        Assert.False(await repo.CurrentAsync(identity,default));
        Assert.Null(await sessions.ReadCurrentAsync("T001","registration-session-"+hash,default));
        Assert.False(await repo.RecoverAsync("T001",recovery,verifier,"replayed-hash",default));
    }
    private static string Required(string name)=>Environment.GetEnvironmentVariable(name)??throw new InvalidOperationException("Current registration database fixture is required.");
}
