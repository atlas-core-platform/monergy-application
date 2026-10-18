using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Monergy.Platform;
using Monergy.Services.Consent;
using Xunit;

namespace Monergy.AccessIntegration.Tests;

public sealed class CustomerSharingReplacementTests
{
    private static CustomerSharingReplacement Change => new(Guid.NewGuid().ToString("N"), 1,
        ["financial-profile.profile.read", "evidence.document.read"], DateTimeOffset.UtcNow.AddDays(7));
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CurrentOrExpiredSharingHasExactOwnerOptionsAndExplicitReplacement(bool expired)
    {
        using var f = new CustomerSharingTests.Fixture();
        if (expired) f.Store.Target = f.Store.Target! with { ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1) };
        var options = await f.Replacement.OptionsAsync("C001", "grant-old", f.Http);
        Assert.Equal("A300", options.Grant.ActorId); Assert.Equal("advisor@example.test", options.Grant.RecipientLabel);
        Assert.Equal(1, options.Sharing.Version); Assert.Equal(0, f.Store.ReplacementWrites);
        var result = await f.Replacement.ReplaceAsync("C001", "grant-old", Change, f.Http);
        Assert.Equal(("grant-old", "Revoked", 2L), (result.Revocation.Id, result.Revocation.Operation, result.Revocation.Version));
        Assert.Equal(("grant-new", "Granted", 3L), (result.Receipt.Id, result.Receipt.Operation, result.Receipt.Version));
        Assert.Equal("A300", f.Store.ReplacementWrite?.ActorId);
        Assert.Equal(["evidence.document.read", "financial-profile.profile.read"], f.Store.ReplacementWrite!.CapabilityIds);
        Assert.Equal(0, f.Handler.Lookups); // The browser cannot redirect the recipient by email.
    }
    [Theory]
    [InlineData("missing", 409)]
    [InlineData("foreign-customer", 503)]
    [InlineData("foreign-id", 503)]
    [InlineData("purpose", 503)]
    [InlineData("self", 409)]
    public async Task InvalidTargetsNeverReachMutation(string kind, int status)
    {
        using var f = new CustomerSharingTests.Fixture(); var target = f.Store.Target!;
        f.Store.Target = kind switch { "missing" => null, "foreign-customer" => target with { CustomerId = "C900" }, "foreign-id" => target with { Id = "another" }, "purpose" => target with { Purpose = "other" }, _ => target with { ActorId = "A100" } };
        Assert.Equal(status, (await Assert.ThrowsAsync<TenantBoundaryException>(() => f.Replacement.ReplaceAsync("C001", "grant-old", Change, f.Http))).Status);
        Assert.Equal(0, f.Store.ReplacementWrites);
    }
    [Fact]
    public async Task RevokedTargetCannotOfferANewChangeButCanReachStoreReplayDecision()
    {
        using var f = new CustomerSharingTests.Fixture(); f.Store.Target = f.Store.Target! with { RevokedAt = DateTimeOffset.UtcNow };
        Assert.Equal(409, (await Assert.ThrowsAsync<TenantBoundaryException>(() => f.Replacement.OptionsAsync("C001", "grant-old", f.Http))).Status);
        await f.Replacement.ReplaceAsync("C001", "grant-old", Change, f.Http);
        Assert.Equal(1, f.Store.ReplacementWrites); // Adapter, not a claim of PostgreSQL replay verification.
    }
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task OwnerChangeBeforeOrAfterWriteCannotConfirm(int changeAt)
    {
        using var f = new CustomerSharingTests.Fixture(); f.Handler.OwnerChangeAt = changeAt;
        Assert.Equal(409, (await Assert.ThrowsAsync<TenantBoundaryException>(() => f.Replacement.ReplaceAsync("C001", "grant-old", Change, f.Http))).Status);
        Assert.Equal(changeAt == 2 ? 0 : 1, f.Store.ReplacementWrites);
    }
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task InactiveRecipientBeforeOrAfterWriteCannotConfirm(int inactiveAt)
    {
        using var f = new CustomerSharingTests.Fixture(); f.Handler.InactiveMemberAt = inactiveAt;
        Assert.Equal(409, (await Assert.ThrowsAsync<TenantBoundaryException>(() => f.Replacement.ReplaceAsync("C001", "grant-old", Change, f.Http))).Status);
        Assert.Equal(inactiveAt == 1 ? 0 : 1, f.Store.ReplacementWrites);
    }
    [Theory]
    [InlineData("old")]
    [InlineData("same")]
    [InlineData("customer")]
    [InlineData("actor")]
    [InlineData("operation")]
    [InlineData("version")]
    [InlineData("evidence")]
    public async Task BothReceiptsMustBeExactlyBoundToTheSelectedReplacement(string mode)
    {
        using var f = new CustomerSharingTests.Fixture(); f.Store.ReceiptMode = mode;
        Assert.Equal(503, (await Assert.ThrowsAsync<TenantBoundaryException>(() => f.Replacement.ReplaceAsync("C001", "grant-old", Change, f.Http))).Status);
        Assert.Equal(1, f.Store.ReplacementWrites);
    }
    [Theory]
    [InlineData("revision")]
    [InlineData("overflow")]
    [InlineData("request")]
    [InlineData("scope")]
    [InlineData("empty")]
    [InlineData("duplicate")]
    [InlineData("expiry")]
    [InlineData("duration")]
    public async Task InvalidSelectionFailsBeforeSessionOrWrite(string mode)
    {
        using var f = new CustomerSharingTests.Fixture(); var change = Change;
        change = mode switch { "revision" => change with { ExpectedVersion = 0 }, "overflow" => change with { ExpectedVersion = long.MaxValue - 1 }, "request" => change with { RequestId = "invalid" }, "scope" => change with { CapabilityIds = ["ai-intelligence.analysis.execute"] }, "empty" => change with { CapabilityIds = [] }, "duplicate" => change with { CapabilityIds = ["evidence.document.read", "evidence.document.read"] }, "expiry" => change with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) }, _ => change with { ExpiresAt = DateTimeOffset.UtcNow.AddDays(91) } };
        Assert.Equal(400, (await Assert.ThrowsAsync<TenantBoundaryException>(() => f.Replacement.ReplaceAsync("C001", "grant-old", change, f.Http))).Status);
        Assert.Equal(0, f.Handler.Sessions); Assert.Equal(0, f.Store.ReplacementWrites);
    }
    [Fact]
    public async Task ActualReplacementHttpRejectsForeignOriginOverridesAndMissingSessions()
    {
        using var f = new CustomerSharingTests.Fixture(); var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Services.AddSingleton(f.Owner); builder.Services.AddSingleton(f.Replacement);
        builder.Services.ConfigureHttpJsonOptions(o => { o.SerializerOptions.UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow; o.SerializerOptions.RespectRequiredConstructorParameters = true; });
        await using var app = builder.Build(); app.MapCustomerSharing(); await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        const string path = "/local/v1/consents/customers/C001/grants/grant-old";
        using var anonymous = await client.GetAsync(path + "/sharing-options"); Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        client.DefaultRequestHeaders.Add("X-Monergy-Tenant", "T001"); client.DefaultRequestHeaders.Add("X-Monergy-Session", "current-session");
        using var options = await client.GetAsync(path + "/sharing-options"); Assert.Equal(HttpStatusCode.OK, options.StatusCode); Assert.Equal("no-store", options.Headers.CacheControl?.ToString());
        using var extra = await client.PostAsJsonAsync(path + "/replacement", new { requestId = Change.RequestId, expectedVersion = 1, Change.CapabilityIds, Change.ExpiresAt, actorId = "A900" }); Assert.Equal(HttpStatusCode.BadRequest, extra.StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Post, path + "/replacement") { Content = JsonContent.Create(Change) }; request.Headers.Add("Origin", "https://foreign.test");
        using var foreign = await client.SendAsync(request); Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode); Assert.Equal(0, f.Store.ReplacementWrites);
        using var valid = await client.PostAsJsonAsync(path + "/replacement", Change); Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        await app.StopAsync();
    }
}
