using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Monergy.Contracts;
using Monergy.Services.CustomerIdentity;
using Monergy.Services.CustomerIdentity.Application;
using Xunit;

namespace Monergy.AccessIntegration.Tests;

public sealed class SelfCustomerProfileTests
{
    private static readonly TenantSessionLookup Lookup = new("T001", "current-session");
    private static SelfCustomerProfileChange Change => new(Guid.NewGuid().ToString("N"), 2, "  Updated Customer  ", " +919876543210 ");

    [Fact]
    public async Task ReadUsesCurrentIdentityAndRechecksMembershipAndOwnership()
    {
        var f = new Fixture();
        var profile = await f.Owner.ReadAsync(Lookup, default);
        Assert.Equal("C001", profile.CustomerId);
        Assert.Equal(2, f.Membership.Calls);
        Assert.Equal(("T001", "A100", "C001"), f.Repository.OwnershipCheck);
    }

    [Theory]
    [InlineData("T002", "current-session")]
    [InlineData("T001", "other-session")]
    public async Task ForeignSessionOrTenantCannotReadOrMutate(string tenant, string session)
    {
        var f = new Fixture(); var lookup = new TenantSessionLookup(tenant, session);
        Assert.Equal(401, (await Assert.ThrowsAsync<TenantAccessException>(() => f.Owner.ReadAsync(lookup, default))).StatusCode);
        await Assert.ThrowsAsync<TenantAccessException>(() => f.Owner.SaveAsync(lookup, Change, default));
        Assert.Equal(0, f.Repository.Reads);
        Assert.Equal(0, f.Repository.Saves);
    }

    [Fact]
    public async Task MissingCustomerDoesNotCreateAnIdentityOrProfile()
    {
        var f = new Fixture(); f.Repository.Profile = null;
        Assert.Equal(403, (await Assert.ThrowsAsync<TenantAccessException>(() => f.Owner.SaveAsync(Lookup, Change, default))).StatusCode);
        Assert.Equal(0, f.Repository.Saves);
    }

    [Theory]
    [InlineData("T002", "A100", "C001")]
    [InlineData("T001", "A900", "C001")]
    [InlineData("T001", "A100", "")]
    public async Task MalformedOwnerBindingCannotReachMutation(string tenant, string actor, string customer)
    {
        var f = new Fixture(); f.Repository.Profile = f.Repository.Profile! with { TenantId = tenant, ActorId = actor, CustomerId = customer };
        Assert.Equal(503, (await Assert.ThrowsAsync<TenantAccessException>(() => f.Owner.SaveAsync(Lookup, Change, default))).StatusCode);
        Assert.Equal(0, f.Repository.Saves);
    }

    [Fact]
    public async Task OwnershipChangeBeforeReadConfirmationHidesTheProfile()
    {
        var f = new Fixture(); f.Repository.Owns = false;
        Assert.Equal(403, (await Assert.ThrowsAsync<TenantAccessException>(() => f.Owner.ReadAsync(Lookup, default))).StatusCode);
    }

    [Fact]
    public async Task SaveBindsTheOriginallyReadCustomerAndNormalizesOnlyEditableFields()
    {
        var f = new Fixture(); var change = Change;
        var receipt = await f.Owner.SaveAsync(Lookup, change, default);
        Assert.Equal("C001", f.Repository.SavedCustomer);
        Assert.Equal("Updated Customer", f.Repository.SavedChange?.DisplayName);
        Assert.Equal("+919876543210", f.Repository.SavedChange?.Mobile);
        Assert.Equal(new(change.OperationId, 3), receipt);
        Assert.Equal(2, f.Membership.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task OptionalMobileCanBeCleared(string? mobile)
    {
        var f = new Fixture();
        await f.Owner.SaveAsync(Lookup, Change with { Mobile = mobile }, default);
        Assert.Null(f.Repository.SavedChange?.Mobile);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Name\nInjected")]
    public async Task InvalidNameFailsBeforePersistence(string name)
    {
        var f = new Fixture();
        Assert.Equal(400, (await Assert.ThrowsAsync<TenantAccessException>(() => f.Owner.SaveAsync(Lookup, Change with { DisplayName = name }, default))).StatusCode);
        Assert.Equal(0, f.Repository.Reads);
    }

    [Theory]
    [InlineData("9876543210")]
    [InlineData("+012345678")]
    [InlineData("+123456")]
    [InlineData("+1234567890123456")]
    [InlineData("+１２３４５６７８９")]
    public async Task InvalidMobileFailsBeforePersistence(string mobile)
    {
        var f = new Fixture();
        Assert.Equal(400, (await Assert.ThrowsAsync<TenantAccessException>(() => f.Owner.SaveAsync(Lookup, Change with { Mobile = mobile }, default))).StatusCode);
        Assert.Equal(0, f.Repository.Saves);
    }

    [Fact]
    public async Task InvalidOperationVersionAndOversizedNameFailBeforePersistence()
    {
        var f = new Fixture();
        foreach (var change in new[] { Change with { OperationId = "invalid" }, Change with { ExpectedVersion = -1 }, Change with { DisplayName = new string('x', 121) } })
            await Assert.ThrowsAsync<TenantAccessException>(() => f.Owner.SaveAsync(Lookup, change, default));
        Assert.Equal(0, f.Repository.Reads);
    }

    [Fact]
    public async Task InactiveMembershipStopsReadAndChangedMembershipRevokesSession()
    {
        var f = new Fixture(); f.Membership.State = _ => new("T001", "A100", false, 3, 8);
        await Assert.ThrowsAsync<TenantAccessException>(() => f.Owner.ReadAsync(Lookup, default));
        Assert.Equal(0, f.Repository.Reads);
        f = new Fixture(); f.Membership.State = call => new("T001", "A100", true, call == 1 ? 3 : 4, 8);
        await Assert.ThrowsAsync<TenantAccessException>(() => f.Owner.ReadAsync(Lookup, default));
        Assert.True(f.Sessions.Revoked);
    }

    [Fact]
    public async Task VersionConflictIsPreservedWithoutConfirmation()
    {
        var f = new Fixture(); f.Repository.Failure = new("PROFILE_VERSION_CONFLICT", 409);
        var failure = await Assert.ThrowsAsync<TenantAccessException>(() => f.Owner.SaveAsync(Lookup, Change, default));
        Assert.Equal("PROFILE_VERSION_CONFLICT", failure.Code);
        Assert.Null(f.Repository.OwnershipCheck);
    }

    [Fact]
    public async Task OwnershipLostAfterCommitDoesNotReturnSuccessOrPretendRollback()
    {
        var f = new Fixture(); f.Repository.Owns = false;
        await Assert.ThrowsAsync<TenantAccessException>(() => f.Owner.SaveAsync(Lookup, Change, default));
        Assert.Equal(1, f.Repository.Saves);
        Assert.NotNull(f.Repository.SavedChange);
    }

    [Fact]
    public async Task ActualHttpBoundaryRejectsUnknownScopeFieldsAndCrossOriginRequests()
    {
        var f = new Fixture();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.ConfigureHttpJsonOptions(o => { o.SerializerOptions.UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow; o.SerializerOptions.RespectRequiredConstructorParameters = true; });
        builder.Services.AddSingleton(f.Owner);
        await using var app = builder.Build(); app.MapSelfCustomerProfile(); await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        using var anonymous = await client.GetAsync("/local/v1/me/customer-profile");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        client.DefaultRequestHeaders.Add("X-Monergy-Tenant", "T001"); client.DefaultRequestHeaders.Add("X-Monergy-Session", "current-session");
        using var read = await client.GetAsync("/local/v1/me/customer-profile");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode); Assert.Equal("no-store", read.Headers.CacheControl?.ToString());
        using var extra = await client.PutAsJsonAsync("/local/v1/me/customer-profile", new { operationId = Change.OperationId, expectedVersion = 2, displayName = "Name", mobile = (string?)null, customerId = "C900", actorId = "A900" });
        Assert.Equal(HttpStatusCode.BadRequest, extra.StatusCode);
        using var cross = new HttpRequestMessage(HttpMethod.Put, "/local/v1/me/customer-profile") { Content = JsonContent.Create(Change) };
        cross.Headers.Add("Origin", "https://foreign.example");
        using var denied = await client.SendAsync(cross);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode); Assert.Equal(0, f.Repository.Saves);
        using var save = await client.PutAsJsonAsync("/local/v1/me/customer-profile", Change);
        Assert.Equal(HttpStatusCode.OK, save.StatusCode); Assert.Equal(1, f.Repository.Saves);
        await app.StopAsync();
    }

    [Theory]
    [InlineData("2000-02-29")]
    [InlineData("1992-04-09")]
    [InlineData("0001-01-01")]
    [InlineData(null)]
    public async Task BirthDateCanBeSavedOrExplicitlyClearedWithoutInferringKyc(string? birthDate)
    {
        var f = new Fixture();
        var change = Change with { DateOfBirth = birthDate };
        await f.Owner.SaveAsync(Lookup, change, default);
        Assert.True(f.Repository.SavedChange!.HasDateOfBirth);
        Assert.Equal(birthDate, f.Repository.SavedChange.DateOfBirth);
        Assert.Equal("C001", f.Repository.SavedCustomer);
        f.Repository.Profile = f.Repository.Profile! with { DateOfBirth = birthDate };
        Assert.Equal(birthDate, (await f.Owner.ReadAsync(Lookup, default)).DateOfBirth);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1992-4-09")]
    [InlineData("2001-02-29")]
    [InlineData("1992-04-09T00:00:00Z")]
    [InlineData("1992-04-09+05:30")]
    [InlineData(" 1992-04-09")]
    [InlineData("0000-01-01")]
    [InlineData("１９９２-０４-０９")]
    [InlineData("2099-01-01")]
    public async Task InvalidBirthDateNeverReachesOwnerStorage(string birthDate)
    {
        var f = new Fixture();
        Assert.Equal("PROFILE_DATE_OF_BIRTH_INVALID", (await Assert.ThrowsAsync<TenantAccessException>(
            () => f.Owner.SaveAsync(Lookup, Change with { DateOfBirth = birthDate }, default))).Code);
        Assert.Equal(0, f.Repository.Reads); Assert.Equal(0, f.Repository.Saves);
    }

    [Fact]
    public async Task BirthDateUsesTheUtcCalendarDayAndDoesNotConvertTheSelectedDate()
    {
        var f = new Fixture(new FixedClock(new DateTimeOffset(2026, 10, 18, 23, 30, 0, TimeSpan.FromHours(-5))));
        await f.Owner.SaveAsync(Lookup, Change with { DateOfBirth = "2026-10-19" }, default);
        Assert.Equal("2026-10-19", f.Repository.SavedChange!.DateOfBirth);
        await Assert.ThrowsAsync<TenantAccessException>(() => f.Owner.SaveAsync(Lookup, Change with { DateOfBirth = "2026-10-20" }, default));
        Assert.Equal(1, f.Repository.Saves);
    }

    [Fact]
    public async Task JsonOmissionAndNullRetainDistinctMeaningThroughRecordNormalization()
    {
        var json = "{\"operationId\":\"" + Change.OperationId + "\",\"expectedVersion\":2,\"displayName\":\"Updated\",\"mobile\":null";
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var omitted = System.Text.Json.JsonSerializer.Deserialize<SelfCustomerProfileChange>(json + "}", options)!;
        var cleared = System.Text.Json.JsonSerializer.Deserialize<SelfCustomerProfileChange>(json + ",\"dateOfBirth\":null}", options)!;
        Assert.False(omitted.HasDateOfBirth); Assert.True(cleared.HasDateOfBirth);
        var f = new Fixture(); f.Repository.Profile = f.Repository.Profile! with { DateOfBirth = "1992-04-09" };
        await f.Owner.SaveAsync(Lookup, omitted, default); Assert.False(f.Repository.SavedChange!.HasDateOfBirth);
        await f.Owner.SaveAsync(Lookup, cleared, default); Assert.True(f.Repository.SavedChange!.HasDateOfBirth); Assert.Null(f.Repository.SavedChange.DateOfBirth);
    }

    [Theory]
    [InlineData("2001-02-29")]
    [InlineData("2099-01-01")]
    [InlineData("1992-04-09T00:00:00Z")]
    public async Task MalformedStoredBirthDateIsNotPresentedOrOverwritten(string birthDate)
    {
        var f = new Fixture(); f.Repository.Profile = f.Repository.Profile! with { DateOfBirth = birthDate };
        Assert.Equal(503, (await Assert.ThrowsAsync<TenantAccessException>(() => f.Owner.ReadAsync(Lookup, default))).StatusCode);
        await Assert.ThrowsAsync<TenantAccessException>(() => f.Owner.SaveAsync(Lookup, Change, default));
        Assert.Equal(0, f.Repository.Saves);
    }

    [Fact]
    public async Task ActualHttpBirthDateWriteRejectsBadDatesAndScopeOverridesAndRetainsOmission()
    {
        var f = new Fixture(); var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.ConfigureHttpJsonOptions(o => { o.SerializerOptions.UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow; o.SerializerOptions.RespectRequiredConstructorParameters = true; });
        builder.Services.AddSingleton(f.Owner);
        await using var app = builder.Build(); app.MapSelfCustomerProfile(); await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        client.DefaultRequestHeaders.Add("X-Monergy-Tenant", "T001"); client.DefaultRequestHeaders.Add("X-Monergy-Session", "current-session");
        async Task<HttpResponseMessage> Put(string fields) => await client.PutAsync("/local/v1/me/customer-profile",
            new StringContent("{\"operationId\":\"" + Change.OperationId + "\",\"expectedVersion\":2,\"displayName\":\"Customer\",\"mobile\":null" + fields + "}", System.Text.Encoding.UTF8, "application/json"));
        foreach (var fields in new[] { ",\"dateOfBirth\":\"2001-02-29\"", ",\"dateOfBirth\":12", ",\"dateOfBirth\":\"1992-04-09\",\"actorId\":\"A900\"", ",\"dateOfBirth\":\"1992-04-09\",\"kycStatus\":\"VERIFIED\"" })
            Assert.Equal(HttpStatusCode.BadRequest, (await Put(fields)).StatusCode);
        Assert.Equal(0, f.Repository.Saves);
        Assert.Equal(HttpStatusCode.OK, (await Put(",\"dateOfBirth\":\"2000-02-29\"")).StatusCode);
        Assert.Equal("2000-02-29", f.Repository.SavedChange!.DateOfBirth);
        Assert.Equal(HttpStatusCode.OK, (await Put("")).StatusCode); Assert.False(f.Repository.SavedChange!.HasDateOfBirth);
        Assert.Equal(HttpStatusCode.OK, (await Put(",\"dateOfBirth\":null")).StatusCode); Assert.True(f.Repository.SavedChange!.HasDateOfBirth);
        f.Repository.Profile = f.Repository.Profile! with { DateOfBirth = "2000-02-29" };
        Assert.Equal("2000-02-29", (await client.GetFromJsonAsync<SelfCustomerProfile>("/local/v1/me/customer-profile"))!.DateOfBirth);
        await app.StopAsync();
    }
    private sealed class FixedClock(DateTimeOffset value) : TimeProvider { public override DateTimeOffset GetUtcNow() => value; }

    private sealed class Fixture
    {
        public Repository Repository { get; } = new();
        public Sessions Sessions { get; } = new();
        public Membership Membership { get; } = new();
        public SelfCustomerProfileApplication Owner { get; }
        public Fixture(TimeProvider? clock = null)
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
                ["Monergy:ExecutionZone"] = "CI_EPHEMERAL", ["Monergy:AccessIntegration:Enabled"] = "true",
                ["Monergy:AccessIntegration:Identities:0:TenantId"] = "T001", ["Monergy:AccessIntegration:Identities:0:ActorId"] = "A100",
                ["Monergy:AccessIntegration:Identities:0:Token"] = new string('a', 64) }).Build();
            Owner = new(Repository, new(Sessions, Membership, new(config)), clock ?? TimeProvider.System);
        }
    }
    // Behavioral dependency doubles. These checks do not execute PostgreSQL SQL.
    private sealed class Repository : ISelfCustomerProfileRepository
    {
        public SelfCustomerProfile? Profile { get; set; } = new("T001", "A100", "C001", "Customer", "owner@example.test", null, 2, null, null);
        public bool Owns { get; set; } = true;
        public int Reads { get; private set; }
        public int Saves { get; private set; }
        public string? SavedCustomer { get; private set; }
        public SelfCustomerProfileChange? SavedChange { get; private set; }
        public (string, string, string)? OwnershipCheck { get; private set; }
        public TenantAccessException? Failure { get; set; }
        public Task<SelfCustomerProfile?> ReadAsync(string tenant, string actor, CancellationToken ct) { Reads++; return Task.FromResult(Profile); }
        public Task<bool> OwnsAsync(string tenant, string actor, string customer, CancellationToken ct) { OwnershipCheck = (tenant, actor, customer); return Task.FromResult(Owns); }
        public Task<SelfCustomerProfileReceipt> SaveAsync(string tenant, string actor, string customer, SelfCustomerProfileChange change, DateTimeOffset now, CancellationToken ct)
        {
            if (Failure is not null) throw Failure;
            Saves++; SavedCustomer = customer; SavedChange = change;
            return Task.FromResult(new SelfCustomerProfileReceipt(change.OperationId, change.ExpectedVersion + 1));
        }
    }
    private sealed class Membership : ITenantMembershipClient
    {
        public int Calls { get; private set; }
        public Func<int, TenantMembershipState> State { get; set; } = _ => new("T001", "A100", true, 3, 8);
        public Task<TenantMembershipState> ReadAsync(string tenantId, string actorId, CancellationToken ct) => Task.FromResult(State(++Calls));
    }
    private sealed class Sessions : ITenantSessionRepository
    {
        public bool Revoked { get; private set; }
        public Task<TenantSessionContext?> ReadCurrentAsync(string tenantId, string sessionId, CancellationToken ct) => Task.FromResult<TenantSessionContext?>(!Revoked && tenantId == "T001" && sessionId == "current-session" ? new("T001", "A100", "current-session", 3, DateTimeOffset.UtcNow.AddMinutes(30)) : null);
        public Task RevokeAsync(string tenantId, string sessionId, CancellationToken ct) { Revoked = true; return Task.CompletedTask; }
        public Task<TenantSessionContext> CreateAsync(string tenantId, string actorId, string sessionId, long subjectVersion, CancellationToken ct) => throw new NotSupportedException();
        public Task<AccessManagementReceipt> ConsumeAsync(string tenant, string destination, AccessManagementEvent message, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> ReadyAsync(CancellationToken ct) => Task.FromResult(true);
    }
}
