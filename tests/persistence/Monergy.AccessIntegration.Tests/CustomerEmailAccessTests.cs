using System.Net;
using System.Net.Http.Json;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Monergy.Contracts;
using Monergy.Services.CustomerIdentity;
using Monergy.Services.CustomerIdentity.Application;
using Monergy.Services.CustomerIdentity.Infrastructure;
using Xunit;

namespace Monergy.AccessIntegration.Tests;

public sealed class CustomerEmailAccessTests
{
    [Fact]
    public async Task RequestsAreOpaqueAndUnknownEmailsHaveTheSameResponseWithoutDelivery()
    {
        var context = new Fixture();
        var known = await context.RequestAsync();
        var unknown = await context.Owner.RequestAsync(new("T001", "unknown@example.test"), default);
        Assert.Equal(64, known.ChallengeId.Length);
        Assert.Equal(known.ExpiresAt, unknown.ExpiresAt);
        Assert.Equal(known.RetryAfterSeconds, unknown.RetryAfterSeconds);
        Assert.NotEqual(known.ChallengeId, unknown.ChallengeId);
        Assert.Single(context.Repository.Rows);
        Assert.DoesNotContain("code", JsonSerializer.Serialize(known), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("email", JsonSerializer.Serialize(known), StringComparison.OrdinalIgnoreCase);
        Assert.Null(context.Sessions.Current);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-email")]
    [InlineData("Person <owner@example.test>")]
    [InlineData("owner@example.test\r\nBcc: other@example.test")]
    public async Task InvalidOrHeaderBearingEmailCannotReachPersistence(string email)
    {
        var context = new Fixture();
        await Assert.ThrowsAsync<TenantAccessException>(() => context.Owner.RequestAsync(new("T001", email), default));
        Assert.Empty(context.Repository.Rows);
    }

    [Fact]
    public void EmailNormalizationIsDeterministicAndTheDeliveryEnvelopeBindsTenantAndChallenge()
    {
        Assert.Equal("owner@example.test", CustomerEmailAccess.NormalizeEmail("OWNER@EXAMPLE.TEST"));
        var protector = new CustomerEmailCodeProtector(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        var bytes = protector.Protect("T001", "hash", "012345");
        Assert.Equal("012345", protector.Unprotect("T001", "hash", bytes));
        Assert.Throws<AuthenticationTagMismatchException>(() => protector.Unprotect("T002", "hash", bytes));
        Assert.Throws<AuthenticationTagMismatchException>(() => protector.Unprotect("T001", "other", bytes));
        Assert.NotEqual(protector.Verifier("T001", "id", "012345"), protector.Verifier("T002", "id", "012345"));
        Assert.Throws<InvalidOperationException>(() => new CustomerEmailCodeProtector("not-a-secret"));
    }

    [Fact]
    public async Task SuccessfulDeliveryThenCodeConfirmationCreatesTheExistingOwnedSession()
    {
        var context = new Fixture();
        var challenge = await context.RequestAsync();
        var code = context.Code(challenge);
        await Assert.ThrowsAsync<TenantAccessException>(() => context.ConfirmAsync(challenge, code));
        await context.DeliverAsync();
        Assert.Equal(code, Assert.Single(context.Sender.Messages).Code);
        var session = await context.ConfirmAsync(challenge, code);
        Assert.Equal("A100", session.ActorId);
        Assert.Equal("T001", session.TenantId);
        Assert.Equal(2, context.Membership.Calls);
        Assert.True(context.Repository.Rows.Values.Single().Consumed);
        Assert.Null(context.Repository.Rows.Values.Single().ProtectedCode);
    }

    [Fact]
    public async Task ConcurrentReplayHasOnlyOneSuccessfulSession()
    {
        var context = new Fixture();
        var challenge = await context.RequestAsync(); var code = context.Code(challenge);
        await context.DeliverAsync();
        async Task<bool> Confirm()
        {
            try { await context.ConfirmAsync(challenge, code); return true; }
            catch (TenantAccessException) { return false; }
        }
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(Confirm)));
        Assert.Single(results, x => x);
        Assert.Equal(1, context.Sessions.Created);
    }

    [Fact]
    public async Task FiveWrongAttemptsLockTheChallengeEvenWithTheCorrectCode()
    {
        var context = new Fixture();
        var challenge = await context.RequestAsync(); var code = context.Code(challenge);
        await context.DeliverAsync();
        var wrong = code == "000000" ? "000001" : "000000";
        for (var i = 0; i < 5; i++) await Assert.ThrowsAsync<TenantAccessException>(() => context.ConfirmAsync(challenge, wrong));
        await Assert.ThrowsAsync<TenantAccessException>(() => context.ConfirmAsync(challenge, code));
        Assert.Equal(0, context.Sessions.Created);
    }

    [Fact]
    public async Task ExpiryAndTenantSubstitutionRejectWithoutMembershipCalls()
    {
        var context = new Fixture();
        var challenge = await context.RequestAsync(); var code = context.Code(challenge);
        await context.DeliverAsync();
        await Assert.ThrowsAsync<TenantAccessException>(() => context.Owner.ConfirmAsync(new("T002", challenge.ChallengeId, code), default));
        context.Clock.Advance(TimeSpan.FromMinutes(5));
        await Assert.ThrowsAsync<TenantAccessException>(() => context.ConfirmAsync(challenge, code));
        Assert.Equal(0, context.Membership.Calls);
    }

    [Theory]
    [InlineData("short", "123456")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "123456")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "12345")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "１２３４５６")]
    public async Task MalformedConfirmationFailsBeforeRepositoryRead(string challenge, string code)
    {
        var context = new Fixture();
        var error = await Assert.ThrowsAsync<TenantAccessException>(() => context.Owner.ConfirmAsync(new("T001", challenge, code), default));
        Assert.Equal("EMAIL_CODE_INVALID", error.Code);
        Assert.Equal(0, context.Repository.Consumes);
    }

    [Fact]
    public async Task CooldownDoesNotReplaceTheChallengeAndLaterResendInvalidatesTheOldCode()
    {
        var context = new Fixture();
        var first = await context.RequestAsync(); var oldCode = context.Code(first);
        await context.RequestAsync();
        Assert.Equal(CustomerEmailCodeProtector.ChallengeHash(first.ChallengeId), context.Repository.Rows.Values.Single().Hash);
        context.Clock.Advance(TimeSpan.FromSeconds(60));
        var replacement = await context.RequestAsync(); var code = context.Code(replacement);
        await context.DeliverAsync();
        await Assert.ThrowsAsync<TenantAccessException>(() => context.ConfirmAsync(first, oldCode));
        await context.ConfirmAsync(replacement, code);
    }

    [Fact]
    public async Task DurableRecipientLimitAllowsOnlyFiveRequestsInOneHour()
    {
        var context = new Fixture();
        CustomerEmailChallenge? last = null;
        for (var i = 0; i < 6; i++) { last = await context.RequestAsync(); context.Clock.Advance(TimeSpan.FromSeconds(61)); }
        var row = context.Repository.Rows.Values.Single();
        Assert.Equal(5, row.Requests);
        Assert.NotEqual(CustomerEmailCodeProtector.ChallengeHash(last!.ChallengeId), row.Hash);
    }

    [Fact]
    public async Task MembershipRevocationAndOwnershipChangesCannotGainAWorkspace()
    {
        var context = new Fixture();
        var challenge = await context.RequestAsync(); var code = context.Code(challenge);
        await context.DeliverAsync();
        context.Membership.Read = _ => new("T001", "A100", false, 3, 8);
        await Assert.ThrowsAsync<TenantAccessException>(() => context.ConfirmAsync(challenge, code));
        Assert.Equal(0, context.Sessions.Created);

        context = new Fixture(); challenge = await context.RequestAsync(); code = context.Code(challenge);
        await context.DeliverAsync(); context.Repository.Current = _ => false;
        await Assert.ThrowsAsync<TenantAccessException>(() => context.ConfirmAsync(challenge, code));
        Assert.Equal(0, context.Sessions.Created);
    }

    [Fact]
    public async Task OwnerChangeAfterSessionIssuanceRevokesTheUnreturnedSession()
    {
        var context = new Fixture();
        var challenge = await context.RequestAsync(); var code = context.Code(challenge);
        await context.DeliverAsync(); context.Repository.Current = call => call == 1;
        await Assert.ThrowsAsync<TenantAccessException>(() => context.ConfirmAsync(challenge, code));
        Assert.True(context.Sessions.Revoked);
    }

    [Fact]
    public async Task MembershipDependencyFailureAfterIssuanceAlsoRevokesTheUnreturnedSession()
    {
        var context = new Fixture();
        var challenge = await context.RequestAsync(); var code = context.Code(challenge);
        await context.DeliverAsync();
        context.Membership.Read = call => call == 1 ? new("T001", "A100", true, 3, 8) : throw new TenantAccessException("MEMBERSHIP_UNAVAILABLE", 503);
        await Assert.ThrowsAsync<TenantAccessException>(() => context.ConfirmAsync(challenge, code));
        Assert.True(context.Sessions.Revoked);
    }

    [Fact]
    public async Task FailedDeliveryRetriesAtMostThreeTimesAndNeverEnablesConfirmation()
    {
        var context = new Fixture();
        var challenge = await context.RequestAsync(); var code = context.Code(challenge);
        context.Sender.Fail = true;
        for (var i = 0; i < 4; i++) { await context.DeliverAsync(); context.Clock.Advance(TimeSpan.FromSeconds(21)); }
        var row = context.Repository.Rows.Values.Single();
        Assert.Equal(3, row.DeliveryAttempt);
        Assert.Null(row.ProtectedCode);
        await Assert.ThrowsAsync<TenantAccessException>(() => context.ConfirmAsync(challenge, code));
    }

    [Fact]
    public void EmailAccessIsDisabledByDefaultAndRejectsIncompleteConfiguration()
    {
        var services = new ServiceCollection();
        Assert.False(services.AddCustomerEmailAccess(new ConfigurationBuilder().Build()));
        var values = Settings(); values["Monergy:CustomerEmailAccess:Enabled"] = "true";
        Assert.Throws<InvalidOperationException>(() => services.AddCustomerEmailAccess(new ConfigurationBuilder().AddInMemoryCollection(values).Build()));
    }

    [Fact]
    public async Task ActualHttpBoundaryUsesGenericResponsesAndRejectsCrossOriginUnknownFieldsAndRateExcess()
    {
        var context = new Fixture();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.Configure<Microsoft.AspNetCore.Routing.RouteHandlerOptions>(
            options => options.ThrowOnBadRequest = false);
        builder.Services.ConfigureHttpJsonOptions(o => { o.SerializerOptions.UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow; o.SerializerOptions.RespectRequiredConstructorParameters = true; });
        builder.Services.AddSingleton(context.Owner);
        builder.Services.AddRateLimiter(o => { o.RejectionStatusCode = 429; o.AddFixedWindowLimiter("customer-email", x => { x.PermitLimit = 6; x.Window = TimeSpan.FromMinutes(1); x.QueueLimit = 0; }); });
        await using var app = builder.Build();
        app.MapCustomerEmailAccess(true);
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = new HttpClient { BaseAddress = new Uri(address) };
        using var request = await client.PostAsJsonAsync("/local/v1/customer-email-access/request", new { tenantId = "T001", email = "owner@example.test" });
        Assert.Equal(HttpStatusCode.OK, request.StatusCode);
        Assert.Equal("no-store", request.Headers.CacheControl?.ToString());
        var challenge = await request.Content.ReadFromJsonAsync<CustomerEmailChallenge>();
        Assert.NotNull(challenge);
        using var wrong = await client.PostAsJsonAsync("/local/v1/customer-email-access/confirm", new { tenantId = "T002", challengeId = challenge.ChallengeId, code = "123456" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        using var extra = await client.PostAsJsonAsync("/local/v1/customer-email-access/request", new { tenantId = "T001", email = "owner@example.test", actorId = "A900" });
        Assert.Equal(HttpStatusCode.BadRequest, extra.StatusCode);
        using var crossRequest = new HttpRequestMessage(HttpMethod.Post, "/local/v1/customer-email-access/request") { Content = JsonContent.Create(new { tenantId = "T001", email = "owner@example.test" }) };
        crossRequest.Headers.Add("Origin", "https://untrusted.example");
        using var cross = await client.SendAsync(crossRequest); Assert.Equal(HttpStatusCode.Forbidden, cross.StatusCode);
        for (var i = 0; i < 3; i++) { using var attempt = await client.PostAsJsonAsync("/local/v1/customer-email-access/request", new { tenantId = "T001", email = "unknown@example.test" }); if (i == 2) Assert.Equal(HttpStatusCode.TooManyRequests, attempt.StatusCode); }
        Assert.Null(context.Sessions.Current);
        await app.StopAsync();
    }

    private static Dictionary<string, string?> Settings() => new()
    {
        ["Monergy:ExecutionZone"] = "CI_EPHEMERAL", ["Monergy:AccessIntegration:Enabled"] = "true",
        ["Monergy:AccessIntegration:Identities:0:TenantId"] = "T001", ["Monergy:AccessIntegration:Identities:0:ActorId"] = "A100",
        ["Monergy:AccessIntegration:Identities:0:Token"] = new string('a', 64),
    };

    private sealed class Fixture
    {
        public Clock Clock { get; } = new();
        public Repository Repository { get; } = new();
        public Sessions Sessions { get; } = new();
        public Membership Membership { get; } = new();
        public Sender Sender { get; } = new();
        public CustomerEmailCodeProtector Protector { get; } = new(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        public CustomerEmailAccess Owner { get; }
        public Fixture() => Owner = new(Repository, Protector, new(Sessions, Membership, new(new ConfigurationBuilder().AddInMemoryCollection(Settings()).Build())), Clock);
        public Task<CustomerEmailChallenge> RequestAsync() => Owner.RequestAsync(new("T001", "OWNER@EXAMPLE.TEST"), default);
        public string Code(CustomerEmailChallenge challenge) { var row = Repository.Rows.Values.Single(x => x.Hash == CustomerEmailCodeProtector.ChallengeHash(challenge.ChallengeId)); return Protector.Unprotect("T001", row.Hash, row.ProtectedCode!); }
        public Task<TenantSessionContext> ConfirmAsync(CustomerEmailChallenge challenge, string code) => Owner.ConfirmAsync(new("T001", challenge.ChallengeId, code), default);
        public Task DeliverAsync() => new CustomerEmailDelivery(Repository, Protector, Sender, new("smtp.example.test", 587, "noreply@example.test", "user", "not-a-real-credential", ["T001"]), Clock, NullLogger<CustomerEmailDelivery>.Instance).DeliverPendingAsync(default);
    }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 10, 18, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }
    private sealed class Sender : ICustomerEmailSender
    {
        public List<(string Email, string Code)> Messages { get; } = [];
        public bool Fail { get; set; }
        public Task SendAsync(string email, string code, CancellationToken ct) { if (Fail) throw new SmtpException("Controlled test failure"); Messages.Add((email, code)); return Task.CompletedTask; }
    }
    private sealed class Membership : ITenantMembershipClient
    {
        public int Calls { get; private set; }
        public Func<int, TenantMembershipState> Read { get; set; } = _ => new("T001", "A100", true, 3, 8);
        public Task<TenantMembershipState> ReadAsync(string tenantId, string actorId, CancellationToken cancellationToken) => Task.FromResult(Read(++Calls));
    }
    private sealed class Sessions : ITenantSessionRepository
    {
        public TenantSessionContext? Current { get; private set; }
        public bool Revoked { get; private set; }
        public int Created { get; private set; }
        public Task<TenantSessionContext> CreateAsync(string tenantId, string actorId, string sessionId, long subjectVersion, CancellationToken cancellationToken) { Created++; Current = new(tenantId, actorId, sessionId, subjectVersion, DateTimeOffset.UtcNow.AddMinutes(30)); return Task.FromResult(Current); }
        public Task<TenantSessionContext?> ReadCurrentAsync(string tenantId, string sessionId, CancellationToken cancellationToken) => Task.FromResult(Current);
        public Task RevokeAsync(string tenantId, string sessionId, CancellationToken cancellationToken) { Revoked = true; return Task.CompletedTask; }
        public Task<AccessManagementReceipt> ConsumeAsync(string trustedTenant, string destination, AccessManagementEvent message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> ReadyAsync(CancellationToken cancellationToken) => Task.FromResult(true);
    }
    private sealed class Row
    {
        public string Hash { get; set; } = "";
        public string CodeHash { get; set; } = "";
        public byte[]? ProtectedCode { get; set; }
        public DateTimeOffset RequestedAt { get; set; }
        public DateTimeOffset Window { get; set; }
        public int Requests { get; set; }
        public int Attempts { get; set; }
        public bool Delivered { get; set; }
        public bool Consumed { get; set; }
        public int DeliveryAttempt { get; set; }
        public DateTimeOffset? Lease { get; set; }
    }
    // In-memory behavioral test adapter, never a production service registration.
    // These tests do not claim execution of PostgresCustomerEmailAccess SQL.
    private sealed class Repository : ICustomerEmailAccessRepository
    {
        private readonly object gate = new();
        private int currentCalls;
        public Dictionary<string, Row> Rows { get; } = new(StringComparer.Ordinal);
        public int Consumes { get; private set; }
        public Func<int, bool> Current { get; set; } = _ => true;
        public List<bool> SessionOutcomes { get; } = [];
        public Task RecordSessionAsync(string tenant, string challengeHash, EmailCustomer customer, bool issued, DateTimeOffset now, CancellationToken ct)
        { SessionOutcomes.Add(issued); return Task.CompletedTask; }
        public Task QueueAsync(string tenant, EmailChallengeWrite c, CancellationToken ct)
        {
            if (tenant != "T001" || c.Email != "owner@example.test") return Task.CompletedTask;
            lock (gate)
            {
                Rows.TryGetValue(c.Email, out var prior);
                if (prior is not null && (prior.RequestedAt > c.Now.AddSeconds(-60) || prior.Window > c.Now.AddHours(-1) && prior.Requests >= 5)) return Task.CompletedTask;
                Rows[c.Email] = new() { Hash = c.ChallengeHash, CodeHash = c.CodeHash, ProtectedCode = c.ProtectedCode, RequestedAt = c.Now, Window = prior is null || prior.Window <= c.Now.AddHours(-1) ? c.Now : prior.Window, Requests = prior is null || prior.Window <= c.Now.AddHours(-1) ? 1 : prior.Requests + 1 };
                return Task.CompletedTask;
            }
        }
        public Task<EmailCustomer?> ConsumeAsync(string tenant, string challengeHash, string codeHash, DateTimeOffset now, CancellationToken ct)
        {
            lock (gate)
            {
                Consumes++;
                var row = tenant == "T001" ? Rows.Values.SingleOrDefault(x => x.Hash == challengeHash) : null;
                if (row is null || row.Consumed || !row.Delivered || row.RequestedAt.AddMinutes(5) <= now || row.Attempts >= 5) return Task.FromResult<EmailCustomer?>(null);
                row.Attempts++; row.Consumed = row.CodeHash == codeHash;
                if (row.Consumed || row.Attempts >= 5) row.ProtectedCode = null;
                return Task.FromResult<EmailCustomer?>(row.Consumed ? new("A100", "C001", 1) : null);
            }
        }
        public Task<bool> IsCurrentAsync(string tenant, EmailCustomer customer, CancellationToken ct) => Task.FromResult(Current(++currentCalls));
        public Task<PendingCustomerEmail?> LeaseAsync(string tenant, DateTimeOffset now, CancellationToken ct)
        {
            lock (gate)
            {
                var row = Rows.Values.FirstOrDefault(x => x.ProtectedCode is not null && !x.Consumed && !x.Delivered && x.RequestedAt.AddMinutes(5) > now && x.DeliveryAttempt < 3 && (x.Lease is null || x.Lease <= now));
                if (row is null) return Task.FromResult<PendingCustomerEmail?>(null);
                row.DeliveryAttempt++; row.Lease = now.AddSeconds(20);
                return Task.FromResult<PendingCustomerEmail?>(new(row.Hash, "owner@example.test", row.ProtectedCode!, row.DeliveryAttempt));
            }
        }
        public Task CompleteDeliveryAsync(string tenant, string challengeHash, int deliveryAttempt, bool sent, DateTimeOffset now, CancellationToken ct)
        {
            lock (gate)
            {
                var row = Rows.Values.Single(x => x.Hash == challengeHash);
                row.Delivered = sent; if (sent || deliveryAttempt >= 3 || row.RequestedAt.AddMinutes(5) <= now) row.ProtectedCode = null;
                return Task.CompletedTask;
            }
        }
    }
}
