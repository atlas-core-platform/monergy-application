using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Monergy.Platform;
using Monergy.Platform.ControlPlane;
using Xunit;

namespace Monergy.Architecture.Tests;

public sealed class PlatformOperationsTests
{
    [Fact]
    public async Task PlatformCredentialActivationIsProtectedSingleUseAndVersioned()
    {
        var path = TemporaryPath();
        try
        {
            using var store = new FilePlatformCredentialStore(path, "a950.platform@example.test", new string('t', 64), new FixedClock());
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.IssueActivationAsync(
                "a950.platform@example.test", "wrong", default));
            var activation = await store.IssueActivationAsync("a950.platform@example.test", new string('t', 64), default);
            const string acceptedPassword = "words without symbols";
            await store.ActivateAsync(activation.Ticket, acceptedPassword, default);
            var authentication = await store.AuthenticateAsync("a950.platform@example.test", acceptedPassword, default);
            Assert.Equal("A950", authentication.ActorId);
            Assert.Equal(1, authentication.CredentialVersion);
            var replay = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.ActivateAsync(activation.Ticket, "Another!Password123", default));
            Assert.Equal("PLATFORM_ACTIVATION_TICKET_INVALID", replay.Message);
            var persisted = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain(acceptedPassword, persisted, StringComparison.Ordinal);
            Assert.DoesNotContain(activation.Ticket, persisted, StringComparison.Ordinal);
            using var document = JsonDocument.Parse(persisted);
            var identityV3 = Convert.FromBase64String(document.RootElement.GetProperty("passwordHash").GetString()!);
            Assert.Equal(0x01, identityV3[0]);
            Assert.Equal(StrongPasswordPolicy.Iterations, BinaryPrimitives.ReadInt32BigEndian(identityV3.AsSpan(5, 4)));
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.ActivateAsync("missing", new string(' ', 15), default));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task PlatformCredentialLocksAfterFiveFailuresAndDoesNotAcceptTheCorrectPasswordWhileLocked()
    {
        var path = TemporaryPath();
        try
        {
            using var store = new FilePlatformCredentialStore(
                path, "a950.platform@example.test", new string('t', 64), new FixedClock());
            var activation = await store.IssueActivationAsync(
                "a950.platform@example.test", new string('t', 64), default);
            await store.ActivateAsync(activation.Ticket, "Platform!Password123", default);

            for (var attempt = 0; attempt < 5; attempt++)
            {
                var denied = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    store.AuthenticateAsync("a950.platform@example.test", "Wrong!Password12345", default));
                Assert.Equal("PLATFORM_CREDENTIAL_INVALID", denied.Message);
            }

            var locked = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.AuthenticateAsync("a950.platform@example.test", "Platform!Password123", default));
            Assert.Equal("PLATFORM_CREDENTIAL_LOCKED", locked.Message);
            var status = await store.StatusAsync(default);
            Assert.Equal(DateTimeOffset.Parse(
                "2026-11-16T12:15:00Z", global::System.Globalization.CultureInfo.InvariantCulture), status.LockedUntil);
            Assert.Contains(await store.AuditAsync(default), item =>
                item.EventType == "PasswordSignIn" && item.Outcome == "Locked");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task IntegrationConfigurationIsVersionedTenantScopedAndContainsReferencesOnly()
    {
        var path = TemporaryPath();
        try
        {
            using var store = new FileIntegrationConfigurationStore(path, new FixedClock());
            var changed = await store.ChangeAsync(new(Guid.NewGuid().ToString("N"), 0, "email", true,
                "Organization", "smtp://mail.example.test:2525", "safe-client", "sender@example.test", null,
                new Dictionary<string, string> { ["tenantId"] = "T001" }, ["smtp-primary"], "Configured", "A950"), default);
            Assert.Equal(1, changed.Revision);
            var provider = Assert.Single(changed.Providers, item => item.Code == "email");
            Assert.Equal("T001", provider.Options["tenantId"]);
            Assert.Equal(["smtp-primary"], provider.SecretReferences);
            Assert.Equal("Configuration complete — not yet tested", provider.Readiness);
            var succeeded = Assert.Single(await store.AuditAsync(default));
            Assert.Equal("Organization", succeeded.Scope);
            Assert.Equal("ConfigurationChanged", succeeded.Action);
            Assert.Equal("Succeeded", succeeded.Result);
            var persisted = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain("password", persisted, StringComparison.OrdinalIgnoreCase);
            var staleOperationId = Guid.NewGuid().ToString("N");
            var stale = await Assert.ThrowsAsync<InvalidOperationException>(() => store.ChangeAsync(new(
                staleOperationId, 0, "ai", true, "Platform", "https://model.example.test/v1", null, null, "model",
                new Dictionary<string, string> { ["providerLabel"] = "LOCAL deterministic", ["notice"] = "LOCAL fixture" },
                ["ai-primary"], "Configured", "A950"), default));
            Assert.Equal("INTEGRATION_CONFIGURATION_CONFLICT", stale.Message);
            var rejected = Assert.Single(await store.AuditAsync(default), item => item.OperationId == staleOperationId);
            Assert.Equal("Platform", rejected.Scope);
            Assert.Equal("ConfigurationChangeRejected", rejected.Action);
            Assert.Equal("Conflict", rejected.Result);
            var repeated = await Assert.ThrowsAsync<InvalidOperationException>(() => store.ChangeAsync(new(
                staleOperationId, 0, "ai", true, "Platform", "https://model.example.test/v1", null, null, "model",
                new Dictionary<string, string> { ["providerLabel"] = "LOCAL deterministic", ["notice"] = "LOCAL fixture" },
                ["ai-primary"], "Configured", "A950"), default));
            Assert.Equal("INTEGRATION_CONFIGURATION_CONFLICT", repeated.Message);
            Assert.Equal(2, (await store.AuditAsync(default)).Count);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task IntegrationSecretsAreEncryptedReplaceableResolvableAndClearable()
    {
        var path = TemporaryPath();
        var key = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        try
        {
            using var store = new FileProtectedIntegrationSecretStore(path, key, new FixedClock());
            var created = await store.SetAsync("provider:email:T001", "first-local-secret", "A950", default);
            Assert.True(created.Configured);
            Assert.Equal("first-local-secret", await store.ResolveAsync("provider:email:T001", default));
            Assert.DoesNotContain("first-local-secret", await File.ReadAllTextAsync(path), StringComparison.Ordinal);
            await store.SetAsync("provider:email:T001", "replacement-local-secret", "A950", default);
            Assert.Equal("replacement-local-secret", await store.ResolveAsync("provider:email:T001", default));
            Assert.DoesNotContain("replacement-local-secret", await File.ReadAllTextAsync(path), StringComparison.Ordinal);
            using var wrongKey = new FileProtectedIntegrationSecretStore(path,
                Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
            var unresolved = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                wrongKey.ResolveAsync("provider:email:T001", default));
            Assert.Equal("INTEGRATION_SECRET_UNRESOLVED", unresolved.Message);
            await store.ClearAsync("provider:email:T001", "A950", default);
            Assert.Null(await store.ResolveAsync("provider:email:T001", default));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task GovernedProviderClientAllowsLegacyOnlyForExplicitNeverAdoptedEnvelope()
    {
        const string token = "runtime-token-longer-than-thirty-two-characters";
        using var neverAdopted = Client(HttpStatusCode.OK, GovernedProviderConfigurationDecision.NeverAdopted(), token);
        var legacy = await neverAdopted.ReadAsync("email", "T001", default);
        Assert.True(legacy.LegacyFallbackAllowed);
        Assert.Equal(GovernedProviderAdoptionStates.NeverAdopted, legacy.State);

        using var disabled = Client(HttpStatusCode.OK, GovernedProviderConfigurationDecision.Unavailable(
            GovernedProviderAdoptionStates.Disabled), token);
        var adopted = await disabled.ReadAsync("email", "T001", default);
        Assert.False(adopted.LegacyFallbackAllowed);
        Assert.Null(adopted.Configuration);

        using var ambiguous404 = Client(HttpStatusCode.NotFound, null, token);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ambiguous404.ReadAsync("email", "T001", default));
        Assert.Equal("GOVERNED_PROVIDER_CONFIGURATION_UNAVAILABLE", failure.Message);
    }

    [Theory]
    [InlineData("ai", "T001")]
    [InlineData("ai", "T002")]
    [InlineData("email", "T001")]
    [InlineData("email", "T002")]
    public async Task GovernedProviderClientBindsPlatformHostWhileKeepingLoopbackAndPurposeCredential(string provider, string tenant)
    {
        const string token = "isolated-governed-runtime-purpose-credential";
        var inspected = 0;
        using var http = new HttpClient(new ResponseHandler(HttpStatusCode.OK,
            GovernedProviderConfigurationDecision.NeverAdopted(), request =>
            {
                Assert.Equal("platform.localhost", request.Headers.Host);
                Assert.Equal(new Uri($"http://127.0.0.1:4173/internal/local/v1/governed-integrations/{provider}?tenant={tenant}"), request.RequestUri);
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Null(request.Content);
                Assert.Equal(token, Assert.Single(request.Headers.GetValues("X-Monergy-Provider-Configuration")));
                Assert.Null(request.Headers.Authorization);
                Assert.False(request.Headers.Contains("Cookie"));
                Assert.False(request.Headers.Contains("X-Monergy-Session"));
                Assert.False(request.Headers.Contains("X-Monergy-Tenant"));
                Assert.False(request.Headers.Contains("X-Monergy-Platform-Session"));
                Assert.False(request.Headers.Contains("X-Monergy-Assistant-Feedback"));
                inspected++;
            })) { BaseAddress = new Uri("http://127.0.0.1:4173/") };
        // Logical control-plane routing is fixed by this client, not by a caller's
        // default Host or a tenant/provider identifier.
        http.DefaultRequestHeaders.Host = "untrusted-default.invalid";
        using var client = new GovernedProviderConfigurationClient(http, token);
        var result = await client.ReadAsync(provider, tenant, default);
        Assert.Equal(GovernedProviderAdoptionStates.NeverAdopted, result.State);
        Assert.True(result.LegacyFallbackAllowed);
        Assert.Equal(1, inspected);
    }

    [Fact]
    public async Task PostureChangesAreVersionedIdempotentAndAuditable()
    {
        var path = TemporaryPath();
        try
        {
            using var store = new FilePlatformOperationsStore(path, ["reporting", "evidence"], new FixedClock());
            var operationId = Guid.NewGuid().ToString("N");
            var change = new PlatformPostureChange(
                operationId, "reporting", PlatformOperationalPosture.Investigating,
                "Validating a release dependency.", "A950", 0);

            var changed = await store.ChangeAsync(change, default);
            var replay = await store.ChangeAsync(change, default);
            using var restoredStore = new FilePlatformOperationsStore(path, ["reporting", "evidence"]);
            var restored = await restoredStore.ReadAsync(default);

            Assert.Equal(1, changed.Version);
            Assert.Equal(1, replay.Version);
            Assert.Equal(1, restored.Version);
            Assert.Single(restored.Services);
            Assert.Single(restored.Receipts);
            Assert.Equal("reporting", restored.Services[0].ServiceId);
            Assert.Equal(PlatformOperationalPosture.Investigating, restored.Services[0].Posture);
            Assert.Equal(operationId, restored.Receipts[0].OperationId);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task StaleUnknownAndAuthorityBypassChangesFailClosed()
    {
        var path = TemporaryPath();
        try
        {
            using var store = new FilePlatformOperationsStore(path, ["reporting"]);
            await store.ChangeAsync(Change("reporting", "A950", 0), default);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.ChangeAsync(Change("reporting", "A950", 0), default));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.ChangeAsync(Change("evidence", "A950", 1), default));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.ChangeAsync(Change("reporting", "A900", 1), default));
            var snapshot = await store.ReadAsync(default);
            Assert.Equal(1, snapshot.Version);
            Assert.Single(snapshot.Receipts);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task OperationReplayCannotBeRepurposed()
    {
        var path = TemporaryPath();
        try
        {
            using var store = new FilePlatformOperationsStore(path, ["reporting"]);
            var operation = Change("reporting", "A950", 0);
            await store.ChangeAsync(operation, default);
            var repurposed = operation with { Posture = PlatformOperationalPosture.Maintenance };
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.ChangeAsync(repurposed, default));
            Assert.Equal("PLATFORM_OPERATION_REPLAY_CONFLICT", failure.Message);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static PlatformPostureChange Change(string service, string actor, long expectedVersion) =>
        new(Guid.NewGuid().ToString("N"), service, PlatformOperationalPosture.Normal,
            "Operational review complete.", actor, expectedVersion);

    private static string TemporaryPath() =>
        Path.Combine(Path.GetTempPath(), "monergy-platform-operations-" + Guid.NewGuid().ToString("N") + ".json");

    private static GovernedProviderConfigurationClient Client(HttpStatusCode status,
        GovernedProviderConfigurationDecision? body, string token)
    {
        var http = new HttpClient(new ResponseHandler(status, body)) { BaseAddress = new Uri("http://127.0.0.1:4173/") };
        return new GovernedProviderConfigurationClient(http, token);
    }

    private sealed class ResponseHandler(HttpStatusCode status, GovernedProviderConfigurationDecision? body,
        Action<HttpRequestMessage>? inspect = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            inspect?.Invoke(request);
            return Task.FromResult(new HttpResponseMessage(status) { Content = body is null ? null : JsonContent.Create(body) });
        }
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() =>
            DateTimeOffset.Parse("2026-11-16T12:00:00Z", global::System.Globalization.CultureInfo.InvariantCulture);
    }
}
