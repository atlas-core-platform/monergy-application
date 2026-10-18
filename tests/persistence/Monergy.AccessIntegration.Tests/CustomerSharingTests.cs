using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Monergy.Platform;
using Monergy.Services.Consent;
using Monergy.Services.CustomerIdentity;
using Xunit;

namespace Monergy.AccessIntegration.Tests;

public sealed class CustomerSharingTests
{
    private static CustomerSharingChange Change => new(Guid.NewGuid().ToString("N"), 1, " ADVISOR@EXAMPLE.TEST ",
        ["evidence.document.read", "financial-profile.profile.read"], DateTimeOffset.UtcNow.AddDays(7));

    [Fact]
    public async Task OptionsAreOwnerBoundAndContainOnlyImplementedCustomerSharingScopes()
    {
        using var f = new Fixture(); var result = await f.Owner.OptionsAsync("C001", f.Http);
        Assert.Equal(("T001", "A100", "C001", 1L), (result.TenantId, result.ActorId, result.CustomerId, result.Version));
        Assert.Equal(90, result.MaximumDays); Assert.Equal("customer-advice", result.Purpose);
        Assert.Equal(5, result.Scopes.Count); Assert.All(result.Scopes, s => Assert.Contains(s.Id, CustomerConsentPolicy.Capabilities));
        Assert.DoesNotContain(result.Scopes, s => s.Id.Contains("update", StringComparison.Ordinal) || s.Id.StartsWith("ai-", StringComparison.Ordinal));
        Assert.Equal(2, f.Handler.Owners); Assert.Equal(0, f.Handler.Lookups);
    }
    [Fact]
    public async Task CustomerEmailIsNormalizedAndResolvedWithTheDedicatedCredentialBeforeExactGrant()
    {
        using var f = new Fixture(); var change = Change;
        var receipt = await f.Owner.GrantAsync("C001", change, f.Http);
        Assert.Equal("advisor@example.test", f.Handler.Email);
        Assert.Equal(new string('d', 32), f.Handler.LookupToken);
        Assert.Equal("T001", f.Store.Tenant); Assert.Equal("A100", f.Store.OwnerActor);
        Assert.Equal("A300", f.Store.Write?.ActorId); Assert.Equal("C001", f.Store.Write?.CustomerId);
        Assert.NotNull(f.Store.Write);
        Assert.Equal(["evidence.document.read", "financial-profile.profile.read"], f.Store.Write.CapabilityIds);
        Assert.Equal(change.RequestId, receipt.RequestId); Assert.Equal("advisor@example.test", receipt.RecipientEmail);
        Assert.Equal(3, f.Handler.Owners); Assert.Equal(2, f.Handler.Memberships);
    }
    [Theory]
    [InlineData("email")]
    [InlineData("headers")]
    [InlineData("scope")]
    [InlineData("future")]
    [InlineData("duplicate")]
    [InlineData("empty")]
    [InlineData("expired")]
    [InlineData("long")]
    [InlineData("operation")]
    [InlineData("version")]
    public async Task InvalidInputCannotReachIdentityOrPersistence(string kind)
    {
        using var f = new Fixture(); var c = Change;
        c = kind switch {
            "email" => c with { RecipientEmail = "not-email" }, "headers" => c with { RecipientEmail = "advisor@example.test\r\nBcc: other@example.test" },
            "scope" => c with { CapabilityIds = ["audit.evidence.read"] }, "future" => c with { CapabilityIds = ["ai-intelligence.analysis.execute"] },
            "duplicate" => c with { CapabilityIds = ["evidence.document.read", "evidence.document.read"] }, "empty" => c with { CapabilityIds = [] },
            "expired" => c with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) }, "long" => c with { ExpiresAt = DateTimeOffset.UtcNow.AddDays(91) },
            "operation" => c with { RequestId = "invalid" }, _ => c with { ExpectedVersion = 0 }
        };
        Assert.Equal(400, (await Assert.ThrowsAsync<TenantBoundaryException>(() => f.Owner.GrantAsync("C001", c, f.Http))).Status);
        Assert.Equal(0, f.Handler.Sessions); Assert.Equal(0, f.Store.Writes);
    }
    [Theory]
    [InlineData("T002", "current-session")]
    [InlineData("T001", "foreign-session")]
    public async Task ForeignTenantOrSessionCannotResolveRecipients(string tenant, string session)
    {
        using var f = new Fixture(); f.Http.Request.Headers["X-Monergy-Tenant"] = tenant; f.Http.Request.Headers["X-Monergy-Session"] = session;
        await Assert.ThrowsAsync<TenantBoundaryException>(() => f.Owner.GrantAsync("C001", Change, f.Http));
        Assert.Equal(0, f.Handler.Lookups); Assert.Equal(0, f.Store.Writes);
    }
    [Fact]
    public async Task ADelegateCannotBecomeTheCustomerOwnerForSharing()
    {
        using var f = new Fixture(); f.Handler.OwnerActor = "A900";
        Assert.Equal(403, (await Assert.ThrowsAsync<TenantBoundaryException>(() => f.Owner.GrantAsync("C001", Change, f.Http))).Status);
        Assert.Equal(0, f.Handler.Lookups); Assert.Equal(0, f.Store.Writes);
    }
    [Theory]
    [InlineData("missing", 409)]
    [InlineData("self", 409)]
    [InlineData("foreign", 503)]
    [InlineData("email", 503)]
    [InlineData("unavailable", 503)]
    public async Task MissingSelfForeignOrUnboundRecipientNeverReachesGrant(string kind, int expected)
    {
        using var f = new Fixture(); f.Handler.RecipientMode = kind;
        Assert.Equal(expected, (await Assert.ThrowsAsync<TenantBoundaryException>(() => f.Owner.GrantAsync("C001", Change, f.Http))).Status);
        Assert.Equal(0, f.Store.Writes);
    }
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task RecipientMembershipIsCheckedBeforeAndAfterCommit(int inactiveAt)
    {
        using var f = new Fixture(); f.Handler.InactiveMemberAt = inactiveAt;
        await Assert.ThrowsAsync<TenantBoundaryException>(() => f.Owner.GrantAsync("C001", Change, f.Http));
        Assert.Equal(inactiveAt == 1 ? 0 : 1, f.Store.Writes);
    }
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task OwnershipChangeBeforeOrAfterCommitDoesNotReturnConfirmation(int changeAt)
    {
        using var f = new Fixture(); f.Handler.OwnerChangeAt = changeAt;
        Assert.Equal(409, (await Assert.ThrowsAsync<TenantBoundaryException>(() => f.Owner.GrantAsync("C001", Change, f.Http))).Status);
        Assert.Equal(changeAt == 2 ? 0 : 1, f.Store.Writes);
    }
    [Fact]
    public async Task OptionsRecheckOwnershipAfterStoreRead()
    {
        using var f = new Fixture(); f.Handler.OwnerChangeAt = 2;
        await Assert.ThrowsAsync<TenantBoundaryException>(() => f.Owner.OptionsAsync("C001", f.Http));
        Assert.Equal(0, f.Handler.Lookups);
    }
    [Theory]
    [InlineData("actor")]
    [InlineData("customer")]
    [InlineData("operation")]
    [InlineData("version")]
    public async Task AnUnboundOwnerReceiptNeverConfirmsSuccess(string kind)
    {
        using var f = new Fixture(); f.Store.ReceiptMode = kind;
        Assert.Equal(503, (await Assert.ThrowsAsync<TenantBoundaryException>(() => f.Owner.GrantAsync("C001", Change, f.Http))).Status);
        Assert.Equal(1, f.Store.Writes);
    }
    [Fact]
    public async Task OptimisticConflictIsPreservedWithoutClaimingCommittedData()
    {
        using var f = new Fixture(); f.Store.Fail = true;
        var error = await Assert.ThrowsAsync<TenantBoundaryException>(() => f.Owner.GrantAsync("C001", Change, f.Http));
        Assert.Equal("CONSENT_VERSION_CONFLICT", error.Code); Assert.Equal(0, f.Store.Writes);
    }
    [Fact]
    public async Task ActualSharingHttpBoundaryRequiresOwnerStrictJsonAndSameOrigin()
    {
        using var f = new Fixture(); var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Services.AddSingleton(f.Owner); builder.Services.AddSingleton(f.Replacement); StrictJson(builder.Services);
        await using var app = builder.Build(); app.MapCustomerSharing(); await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        using var anonymous = await client.GetAsync("/local/v1/consents/customers/C001/sharing-options"); Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        client.DefaultRequestHeaders.Add("X-Monergy-Tenant", "T001"); client.DefaultRequestHeaders.Add("X-Monergy-Session", "current-session");
        using var options = await client.GetAsync("/local/v1/consents/customers/C001/sharing-options"); Assert.Equal(HttpStatusCode.OK, options.StatusCode); Assert.Equal("no-store", options.Headers.CacheControl?.ToString());
        using var extra = await client.PostAsJsonAsync("/local/v1/consents/customers/C001/sharing", new { requestId = Change.RequestId, expectedVersion = 1, recipientEmail = "advisor@example.test", capabilityIds = Change.CapabilityIds, expiresAt = Change.ExpiresAt, actorId = "A900" }); Assert.Equal(HttpStatusCode.BadRequest, extra.StatusCode);
        using var cross = new HttpRequestMessage(HttpMethod.Post, "/local/v1/consents/customers/C001/sharing") { Content = JsonContent.Create(Change) }; cross.Headers.Add("Origin", "https://foreign.example");
        using var denied = await client.SendAsync(cross); Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode); Assert.Equal(0, f.Store.Writes);
        using var created = await client.PostAsJsonAsync("/local/v1/consents/customers/C001/sharing", Change); Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        await app.StopAsync();
    }
    [Fact]
    public async Task ActualIdentityLookupHttpUsesItsOwnTokenAndRejectsForeignBinding()
    {
        var directory = new Directory(); var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<IConsentRecipientDirectory>(directory); StrictJson(builder.Services);
        await using var app = builder.Build(); app.MapConsentRecipientLookup(new string('d', 32)); await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) }; client.DefaultRequestHeaders.Add("X-Monergy-Tenant", "T001");
        using var denied = await client.PostAsJsonAsync("/internal/v1/consent-recipients/resolve", new { email = "advisor@example.test" }); Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode); Assert.Equal(0, directory.Calls);
        client.DefaultRequestHeaders.Add("X-Monergy-Owner-Token", new string('d', 32));
        using var found = await client.PostAsJsonAsync("/internal/v1/consent-recipients/resolve", new { email = " ADVISOR@EXAMPLE.TEST " }); Assert.Equal(HttpStatusCode.OK, found.StatusCode); Assert.Equal("advisor@example.test", directory.Email);
        directory.Foreign = true;
        using var foreign = await client.PostAsJsonAsync("/internal/v1/consent-recipients/resolve", new { email = "advisor@example.test" }); Assert.Equal(HttpStatusCode.ServiceUnavailable, foreign.StatusCode);
        using var extra = await client.PostAsJsonAsync("/internal/v1/consent-recipients/resolve", new { email = "advisor@example.test", tenantId = "T002" }); Assert.Equal(HttpStatusCode.BadRequest, extra.StatusCode);
        await app.StopAsync();
    }
    private static void StrictJson(IServiceCollection services) => services.ConfigureHttpJsonOptions(o => { o.SerializerOptions.UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow; o.SerializerOptions.RespectRequiredConstructorParameters = true; o.SerializerOptions.RespectNullableAnnotations = true; });
    internal sealed class Fixture : IDisposable
    {
        public OwnerHandler Handler { get; } = new(); public Store Store { get; } = new(); public DefaultHttpContext Http { get; } = new();
        private readonly HttpClient client; private readonly CustomerOwnerClient owners; public CustomerSharingApplication Owner { get; } public CustomerSharingReplacementApplication Replacement { get; }
        public Fixture()
        {
            Http.Request.Headers["X-Monergy-Tenant"] = "T001"; Http.Request.Headers["X-Monergy-Session"] = "current-session";
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
                ["Monergy:ExecutionZone"] = "CI_EPHEMERAL", ["Monergy:AccessIntegration:Enabled"] = "true", ["Monergy:AccessIntegration:CustomerIdentityUrl"] = "http://127.0.0.1:5101/", ["Monergy:AccessIntegration:AccessManagementUrl"] = "http://127.0.0.1:5088/",
                ["Monergy:AccessIntegration:CustomerContextToken"] = new string('a', 32), ["Monergy:AccessIntegration:CustomerIdentityToken"] = new string('b', 32), ["Monergy:AccessIntegration:MembershipToken"] = new string('c', 32), ["Monergy:AccessIntegration:RecipientLabelToken"] = new string('d', 32) }).Build();
            client = new(Handler); owners = new(config, client); Owner = new(owners, Store, TimeProvider.System); Replacement = new(owners, Store, TimeProvider.System);
        }
        public void Dispose() { owners.Dispose(); client.Dispose(); }
    }
    // Dependency adapters exercise coordination and actual HTTP boundaries;
    // they do not execute the PostgreSQL grant/receipt/audit/outbox transaction.
    internal sealed class Store : ICustomerSharingStore, ICustomerSharingReplacementStore
    {
        public int Writes { get; private set; } public string? Tenant { get; private set; } public string? OwnerActor { get; private set; } public ConsentGrantWrite? Write { get; private set; }
        public bool Fail { get; set; } public string? ReceiptMode { get; set; }
        public ConsentGrant? Target { get; set; } = new("grant-old", "C001", "A300", "customer-advice", ["evidence.document.read"], DateTimeOffset.UtcNow.AddDays(1), null);
        public ConsentReplacementWrite? ReplacementWrite { get; private set; }
        public int ReplacementWrites { get; private set; }
        public Task<ConsentOverview> ReadReplacementAsync(string tenant, string customer, string grant, CancellationToken ct) => Task.FromResult(new ConsentOverview(1, Target is null ? [] : [Target]));
        public Task<ConsentReplacementReceipt> ReplaceAsync(string tenant, CustomerOwnerContext owner, string ownerActor, ConsentReplacementWrite request, CancellationToken ct)
        {
            if (Fail) throw new TenantBoundaryException("CONSENT_VERSION_CONFLICT", 409);
            ReplacementWrites++; ReplacementWrite = request;
            var revoked = new ConsentReceipt(ReceiptMode == "old" ? "wrong" : request.GrantId, request.CustomerId, request.ActorId, "Revoked", request.ExpectedVersion + 1, "old-evidence");
            var granted = new ConsentReceipt(ReceiptMode == "same" ? request.GrantId : "grant-new", ReceiptMode == "customer" ? "C900" : request.CustomerId,
                ReceiptMode == "actor" ? "A900" : request.ActorId, ReceiptMode == "operation" ? "Revoked" : "Granted", ReceiptMode == "version" ? 99 : request.ExpectedVersion + 2, ReceiptMode == "evidence" ? "old-evidence" : "new-evidence");
            return Task.FromResult(new ConsentReplacementReceipt(request.RequestId, revoked, granted));
        }
        public Task<ConsentOverview> ListAsync(string tenant, string customer, CancellationToken ct) => Task.FromResult(new ConsentOverview(1, []));
        public Task<ConsentReceipt> GrantAsync(string tenant, CustomerOwnerContext owner, string ownerActor, ConsentGrantWrite request, CancellationToken ct)
        {
            if (Fail) throw new TenantBoundaryException("CONSENT_VERSION_CONFLICT", 409);
            Writes++; Tenant = tenant; OwnerActor = ownerActor; Write = request;
            return Task.FromResult(new ConsentReceipt("grant-001", ReceiptMode == "customer" ? "C900" : request.CustomerId,
                ReceiptMode == "actor" ? "A900" : request.ActorId, ReceiptMode == "operation" ? "Revoked" : "Granted", ReceiptMode == "version" ? 99 : request.ExpectedVersion + 1, "consent-test-evidence"));
        }
    }
    private sealed class Directory : IConsentRecipientDirectory
    {
        public int Calls { get; private set; } public string? Email { get; private set; } public bool Foreign { get; set; }
        public Task<CustomerOwnerRecipientLabel?> ResolveAsync(string tenant, string email, CancellationToken ct) { Calls++; Email = email; return Task.FromResult<CustomerOwnerRecipientLabel?>(new(Foreign ? "T002" : tenant, "A300", email)); }
    }
    internal sealed class OwnerHandler : HttpMessageHandler
    {
        public int Owners { get; private set; } public int Sessions { get; private set; } public int Lookups { get; private set; } public int Memberships { get; private set; }
        public string? Email { get; private set; } public string? LookupToken { get; private set; }
        public string OwnerActor { get; set; } = "A100"; public string? RecipientMode { get; set; }
        public int InactiveMemberAt { get; set; } public int OwnerChangeAt { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Headers.GetValues("X-Monergy-Tenant").Single() != "T001") return Reply(new { }, HttpStatusCode.Unauthorized);
            if (path.EndsWith("/validate", StringComparison.Ordinal))
            {
                Sessions++; var body = await request.Content!.ReadFromJsonAsync<JsonElement>(ct);
                if (body.GetProperty("authenticationContextId").GetString() != "current-session") return Reply(new { }, HttpStatusCode.Unauthorized);
                return Reply(new CustomerOwnerSession("T001", "A100", "current-session", 3, DateTimeOffset.UtcNow.AddMinutes(30)));
            }
            if (path.Contains("/customer-context/", StringComparison.Ordinal)) { Owners++; return Reply(new CustomerOwnerContext("T001", "C001", OwnerActor, OwnerChangeAt > 0 && Owners >= OwnerChangeAt ? 2 : 1, true)); }
            if (path.Contains("/access-subjects/", StringComparison.Ordinal)) { Memberships++; return Reply(new CustomerOwnerMembership("T001", "A300", Memberships != InactiveMemberAt, 3, 8)); }
            if (path.Contains("/consent-recipient-label/", StringComparison.Ordinal)) return Reply(new CustomerOwnerRecipientLabel("T001", "A300", "advisor@example.test"));
            Lookups++; LookupToken = request.Headers.GetValues("X-Monergy-Owner-Token").Single(); var input = await request.Content!.ReadFromJsonAsync<JsonElement>(ct); Email = input.GetProperty("email").GetString();
            if (RecipientMode == "missing") return Reply(new { }, HttpStatusCode.NotFound);
            if (RecipientMode == "unavailable") return Reply(new { }, HttpStatusCode.ServiceUnavailable);
            return Reply(new CustomerOwnerRecipientLabel(RecipientMode == "foreign" ? "T002" : "T001", RecipientMode == "self" ? "A100" : "A300", RecipientMode == "email" ? "foreign@example.test" : Email!));
        }
        private static HttpResponseMessage Reply(object value, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = JsonContent.Create(value) };
    }
}
