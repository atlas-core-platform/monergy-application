using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Monergy.Contracts;
using Monergy.Services.CustomerIdentity.Application;
using Xunit;
namespace Monergy.AccessIntegration.Tests;
public sealed class CustomerRegistrationTests
{
    [Theory][InlineData("")][InlineData("short")][InlineData("              ")][InlineData("long-password\nsecret")]
    public void InvalidPasswordIsRejected(string value)=>Assert.Throws<TenantAccessException>(()=>new CustomerPasswordPolicy().Hash(value));
    [Fact]public void PasswordHashUsesUniqueSaltAndPreservesWhitespace()
    {var p=new CustomerPasswordPolicy();var a=p.Hash("a long password value");Assert.NotEqual(a,p.Hash("a long password value"));Assert.True(p.Verify(a,"a long password value"));Assert.False(p.Verify(a,"a long password value "));Assert.False(p.Verify(null,"a long password value"));}
    [Fact]public async Task RegistrationRequiresCurrentPublicationBeforeIssuingChallenge()
    {var f=new Fixture();await Assert.ThrowsAsync<TenantAccessException>(()=>f.Owner.RequestAsync(new("T002","person@example.test"),false,default));Assert.Null(f.Repo.Code);}
    [Fact]public async Task ChallengeIsOpaqueAndPurposeBound()
    {var f=new Fixture();var c=await f.Owner.RequestAsync(new("T001","person@example.test"),false,default);var r=f.Repo.Code!;Assert.Equal(64,c.ChallengeId.Length);Assert.Equal("registration",r.Purpose);Assert.Equal(34,r.ProtectedCode.Length);Assert.Throws<AuthenticationTagMismatchException>(()=>f.Protector.Unprotect("T001:recovery",r.ChallengeHash,r.ProtectedCode));}
    [Theory][InlineData("terms")][InlineData("privacy")][InlineData("consent")]
    public async Task EveryAgreementMustBeExplicit(string missing)
    {var f=new Fixture();var r=f.Confirmation() with{AcceptTerms=missing!="terms",AcknowledgePrivacy=missing!="privacy",ConsentToProcessing=missing!="consent"};await Assert.ThrowsAsync<TenantAccessException>(()=>f.Owner.ConfirmAsync(r,default));Assert.Equal(0,f.Repo.Registrations);}
    [Fact]public async Task ChangedPublicationCannotCreateIdentity()
    {var f=new Fixture();await Assert.ThrowsAsync<TenantAccessException>(()=>f.Owner.ConfirmAsync(f.Confirmation() with{AgreementRevision=new string('f',64)},default));Assert.Equal(0,f.Repo.Registrations);}
    [Fact]public async Task ConfirmationCreatesOwnerIdentityThenEnrollsWithoutCreatingSession()
    {var f=new Fixture();var r=await f.Owner.ConfirmAsync(f.Confirmation(),default);Assert.True(r.Active);Assert.Equal("ci-new",r.ActorId);Assert.True(f.Repo.Identity.Enrolled);Assert.Equal(1,f.Enrollment.Calls);Assert.Equal(0,f.Sessions.Created);}
    [Fact]public async Task LostEnrollmentCanResumeWithPasswordWithoutRecreatingIdentity()
    {var f=new Fixture();f.Enrollment.Fail=true;await Assert.ThrowsAsync<TenantAccessException>(()=>f.Owner.ConfirmAsync(f.Confirmation(),default));Assert.False(f.Repo.Identity.Enrolled);f.Enrollment.Fail=false;var s=await f.Owner.SignInAsync(new("T001","person@example.test",Fixture.Password),default);Assert.Equal("ci-new",s.ActorId);Assert.Equal(1,f.Repo.Registrations);Assert.Equal(2,f.Enrollment.Calls);}
    [Fact]public async Task WrongPasswordCannotEnrollOrIssueSession()
    {var f=new Fixture();await Assert.ThrowsAsync<TenantAccessException>(()=>f.Owner.SignInAsync(new("T001","person@example.test","wrong password"),default));Assert.Equal(0,f.Enrollment.Calls);Assert.Equal(0,f.Sessions.Created);Assert.False(f.Repo.LastValid);}
    [Fact]public async Task DisabledMembershipCannotBeRestoredBySigningIn()
    {var f=new Fixture();f.Repo.Identity=f.Repo.Identity with{Enrolled=true};f.Membership.Active=false;await Assert.ThrowsAsync<TenantAccessException>(()=>f.Owner.SignInAsync(new("T001","person@example.test",Fixture.Password),default));Assert.Equal(0,f.Enrollment.Calls);Assert.Equal(0,f.Sessions.Created);}
    [Fact]public async Task RecoveryRaceAfterSessionCreationRevokesTheNewSession()
    {var f=new Fixture();f.Repo.Identity=f.Repo.Identity with{Enrolled=true};f.Sessions.AfterCreate=()=>f.Repo.Current=false;await Assert.ThrowsAsync<TenantAccessException>(()=>f.Owner.SignInAsync(new("T001","person@example.test",Fixture.Password),default));Assert.Equal(1,f.Sessions.Revoked);}
    [Fact]public async Task RecoveryUsesDifferentVerifierAndDoesNotIssueSession()
    {var f=new Fixture();var c=await f.Owner.RequestAsync(new("T001","person@example.test"),true,default);var row=f.Repo.Code!;var code=f.Protector.Unprotect("T001:recovery",row.ChallengeHash,row.ProtectedCode);await f.Owner.RecoverAsync(new("T001",c.ChallengeId,code,"another long password"),default);Assert.Equal(row.CodeHash,f.Repo.RecoveryVerifier);Assert.True(f.Passwords.Verify(f.Repo.RecoveredHash,"another long password"));Assert.Equal(0,f.Sessions.Created);}
    private sealed class Fixture
    {
        public const string Password="a long password value";
        public readonly CustomerPasswordPolicy Passwords=new();
        public readonly CustomerEmailCodeProtector Protector=new(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        public readonly Repository Repo;public readonly Enrollment Enrollment=new();public readonly Sessions Sessions=new();public readonly Membership Membership=new();public readonly CustomerRegistration Owner;
        public Fixture()
        {
            var settings=new Dictionary<string,string?>{{"Monergy:ExecutionZone","CI_EPHEMERAL"},{"Monergy:AccessIntegration:Enabled","true"},{"Monergy:AccessIntegration:Identities:0:TenantId","T001"},{"Monergy:AccessIntegration:Identities:0:ActorId","ci-new"},{"Monergy:AccessIntegration:Identities:0:Token",new string('a',32)},{"Monergy:AccountAgreements:Publications:T001:TenantId","T001"},{"Monergy:AccountAgreements:Publications:T001:PublishedAt","2026-01-01T00:00:00.0000000+00:00"}};
            var kinds=new[]{"CONSENT","PRIVACY","TERMS"};for(var i=0;i<3;i++){var prefix=$"Monergy:AccountAgreements:Publications:T001:Documents:{i}:";settings[prefix+"Kind"]=kinds[i];settings[prefix+"Version"]="test-v1";settings[prefix+"Title"]=kinds[i];settings[prefix+"Body"]="Test fixture only";settings[prefix+"Sha256"]=AccountAgreementCatalog.Hash("Test fixture only");}
            var config=new ConfigurationBuilder().AddInMemoryCollection(settings).Build();Repo=new(Passwords.Hash(Password));
            Owner=new(Repo,Protector,Passwords,new AccountAgreementCatalog(config),Enrollment,new TenantSessionAuthority(Sessions,Membership,new ReferenceTenantAuthenticator(config)),TimeProvider.System);
        }
        public RegistrationConfirmation Confirmation()=>new("T001",new string('a',64),"123456","New customer",Password,Owner.Publication("T001").Revision,true,true,true);
    }
    private sealed class Repository(string password):ICustomerRegistrationRepository
    {
        public RegistrationIdentity Identity=new("T001","person@example.test","ci-new","customer-new",password,1,false);
        public RegistrationCode? Code;public bool Current=true,LastValid;public int Registrations;public string? RecoveryVerifier,RecoveredHash;
        public Task QueueAsync(string tenant,RegistrationCode code,CancellationToken ct){Code=code;return Task.CompletedTask;}
        public Task<RegistrationIdentity?> RegisterAsync(string tenant,string challenge,string verifier,string name,string passwordHash,AccountAgreementPublication publication,CancellationToken ct){Registrations++;Identity=Identity with{PasswordHash=passwordHash};return Task.FromResult<RegistrationIdentity?>(Identity);}
        public Task<RegistrationIdentity?> ReadForSignInAsync(string tenant,string email,CancellationToken ct)=>Task.FromResult<RegistrationIdentity?>(Identity.TenantId==tenant&&Identity.Email==email?Identity:null);
        public Task<bool> RecordSignInAsync(RegistrationIdentity identity,bool valid,CancellationToken ct){LastValid=valid;return Task.FromResult(true);}
        public Task<bool> CurrentAsync(RegistrationIdentity identity,CancellationToken ct)=>Task.FromResult(Current);
        public Task MarkEnrolledAsync(RegistrationIdentity identity,CancellationToken ct){Identity=Identity with{Enrolled=true};return Task.CompletedTask;}
        public Task<bool> RecoverAsync(string tenant,string challenge,string verifier,string passwordHash,CancellationToken ct){RecoveryVerifier=verifier;RecoveredHash=passwordHash;return Task.FromResult(true);}
    }
    private sealed class Enrollment:ICustomerEnrollmentClient
    {public bool Fail;public int Calls;public Task EnrollAsync(string tenant,string actor,string email,CancellationToken ct){Calls++;if(Fail)throw new TenantAccessException("ENROLLMENT_UNAVAILABLE",503);return Task.CompletedTask;}}
    private sealed class Membership:ITenantMembershipClient
    {public bool Active=true;public Task<TenantMembershipState> ReadAsync(string tenantId,string actorId,CancellationToken cancellationToken)=>Task.FromResult(new TenantMembershipState(tenantId,actorId,Active,1,1));}
    private sealed class Sessions:ITenantSessionRepository
    {
        public int Created,Revoked;public Action? AfterCreate;
        public Task<TenantSessionContext> CreateAsync(string tenantId,string actorId,string sessionId,long subjectVersion,CancellationToken cancellationToken){Created++;AfterCreate?.Invoke();return Task.FromResult(new TenantSessionContext(tenantId,actorId,sessionId,subjectVersion,DateTimeOffset.UtcNow.AddMinutes(30)));}
        public Task<TenantSessionContext?> ReadCurrentAsync(string tenantId,string sessionId,CancellationToken cancellationToken)=>throw new NotSupportedException();
        public Task RevokeAsync(string tenantId,string sessionId,CancellationToken cancellationToken){Revoked++;return Task.CompletedTask;}
        public Task<AccessManagementReceipt> ConsumeAsync(string trustedTenant,string destination,AccessManagementEvent message,CancellationToken cancellationToken)=>throw new NotSupportedException();
        public Task<bool> ReadyAsync(CancellationToken cancellationToken)=>Task.FromResult(true);
    }
}
