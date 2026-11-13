using Dapper;
using Microsoft.Extensions.Configuration;
using Monergy.Platform;
using Monergy.Services.ApplicationCase.Application;
using Monergy.Services.ApplicationCase.Infrastructure;
using Npgsql;
using Xunit;

namespace Monergy.ApplicationCase.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ApplicationCasePhysicalOwners
{
    public const string Name = "ApplicationCasePhysicalOwners";
}

[Trait("Category", "Physical")]
[Collection(ApplicationCasePhysicalOwners.Name)]
public sealed class PostgresAssistanceCaseApplicationTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 10, 0, 0, TimeSpan.Zero);

    public async Task InitializeAsync()
    {
        foreach (var tenant in new[] { "T001", "T002" })
        {
            await using var owner = new NpgsqlConnection(Required($"P3_APPLICATION_CASE_{tenant}_OWNER"));
            await owner.OpenAsync();
            await owner.ExecuteAsync("""
                TRUNCATE application_case.advisor_engagement_activities,
                    application_case.advisor_engagement_operations,
                    application_case.advisor_engagements,
                    application_case.assistance_case_events,
                    application_case.assistance_case_operations,
                    application_case.assistance_cases;
                """);
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task CaseAndHistorySurviveOwnerReconstruction()
    {
        var customer = Customer("T001", "A100", "C001");
        var dsa = Dsa("T001", "D300");
        AssistanceCaseView created;
        await using (var initial = Owner())
        {
            created = await initial.CreateAsync(customer, new(Op('a')), Confirm(customer), default);
            await initial.AcceptAsync(created.CaseId, dsa, new(Op('b'), 1), Confirm(dsa), default);
        }

        await using var reconstructed = Owner();
        var durable = Assert.Single(await reconstructed.ListCustomerAsync(customer, default));
        var history = await reconstructed.HistoryAsync(created.CaseId, customer, default);
        Assert.Equal("Accepted", durable.State);
        Assert.Equal("Durable test data", durable.DataDurability);
        Assert.Equal(["Submitted", "Accepted"], history.Events.Select(value => value.Action));
        Assert.Equal("Durable test data", history.DataDurability);
    }

    [Fact]
    public async Task TenantsAreIsolatedAndWrongBindingFailsClosed()
    {
        await using var owner = Owner();
        var created = await owner.CreateAsync(Customer("T001", "A100", "C001"),
            new(Op('a')), Confirm(Customer("T001", "A100", "C001")), default);
        Assert.Empty(await owner.ListDsaAsync(Dsa("T002", "D300"), default));
        Assert.Equal("CASE_NOT_FOUND", (await Assert.ThrowsAsync<ApplicationCaseException>(() =>
            owner.HistoryAsync(created.CaseId, Customer("T002", "A200", "C002"), default))).Code);

        var values = ConfigurationValues();
        values["Monergy:Persistence:ApplicationCase:RuntimeConnections:T002"] =
            Required("P3_APPLICATION_CASE_T001_RUNTIME");
        await using var wrong = new PostgresAssistanceCaseApplication(
            new ConfigurationBuilder().AddInMemoryCollection(values).Build(), new FixedTimeProvider(Now));
        Assert.Equal("CASE_BINDING_INVALID", (await Assert.ThrowsAsync<ApplicationCaseException>(() =>
            wrong.ListDsaAsync(Dsa("T002", "D300"), default))).Code);
    }

    [Fact]
    public async Task ConcurrentAcceptAndRetryHaveOneDurableDecision()
    {
        await using var owner = Owner();
        var customer = Customer("T001", "A100", "C001");
        var created = await owner.CreateAsync(customer, new(Op('a')), Confirm(customer), default);
        var first = Dsa("T001", "D300");
        var second = Dsa("T001", "D301");
        var attempts = await Task.WhenAll(
            Attempt(() => owner.AcceptAsync(created.CaseId, first, new(Op('b'), 1), Confirm(first), default)),
            Attempt(() => owner.AcceptAsync(created.CaseId, second, new(Op('c'), 1), Confirm(second), default)));
        var winner = Assert.Single(attempts.OfType<AssistanceCaseView>());
        Assert.Single(attempts.OfType<ApplicationCaseException>());
        var winningDsa = attempts[0] is AssistanceCaseView ? first : second;
        var winningOperation = attempts[0] is AssistanceCaseView ? Op('b') : Op('c');
        var replay = await owner.AcceptAsync(created.CaseId, winningDsa,
            new(winningOperation, 1), Confirm(winningDsa), default);
        Assert.Equal(winner.CaseId, replay.CaseId);

        await using var db = new NpgsqlConnection(Required("P3_APPLICATION_CASE_T001_OWNER"));
        await db.OpenAsync();
        Assert.Equal(2, await db.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM application_case.assistance_case_events WHERE case_id=@Id;", new { Id = created.CaseId }));
        Assert.Equal(1, await db.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM application_case.assistance_case_operations WHERE case_id=@Id AND action='ACCEPT';", new { Id = created.CaseId }));
    }

    [Fact]
    public async Task RuntimeCanAppendButCannotRewriteCaseHistoryOrBinding()
    {
        var customer = Customer("T001", "A100", "C001");
        await using var owner = Owner();
        _ = await owner.CreateAsync(customer, new(Op('a')), Confirm(customer), default);
        await using var runtime = new NpgsqlConnection(Required("P3_APPLICATION_CASE_T001_RUNTIME"));
        await runtime.OpenAsync();
        foreach (var sql in new[]
        {
            "UPDATE application_case.assistance_case_events SET action='Accepted';",
            "DELETE FROM application_case.assistance_case_events;",
            "UPDATE application_case.assistance_case_operations SET action='ACCEPT';",
            "DELETE FROM application_case.assistance_case_operations;",
            "UPDATE application_case.assistance_cases SET customer_id='forged';",
            "DELETE FROM application_case.assistance_cases;",
            "UPDATE application_case.advisor_engagement_activities SET body='forged';",
            "DELETE FROM application_case.advisor_engagement_activities;",
            "UPDATE application_case.advisor_engagement_operations SET action='NOTE';",
            "DELETE FROM application_case.advisor_engagement_operations;",
            "UPDATE application_case.advisor_engagements SET customer_id='forged';",
            "DELETE FROM application_case.advisor_engagements;",
            "UPDATE application_case.tenant_identity SET tenant_id='T002';",
        }) await Assert.ThrowsAsync<PostgresException>(() => runtime.ExecuteAsync(sql));
    }

    [Fact]
    public async Task AdvisorEngagementIsDurableIdempotentAndTenantIsolated()
    {
        var advisor = Advisor("T001", "A300", "C001");
        AdvisorEngagementView first;
        await using (var owner = AdvisorOwner())
        {
            first = await owner.AddNoteAsync(advisor,
                new(Op('d'), 0, "Prepare affordability discussion", ["fact-income-1"]),
                Confirm(advisor), default);
            var replay = await owner.AddNoteAsync(advisor,
                new(Op('d'), 0, "Prepare affordability discussion", ["fact-income-1"]),
                Confirm(advisor), default);
            Assert.Equal(1, replay.Version);
            Assert.Single(replay.Activities);
        }

        await using var reconstructed = AdvisorOwner();
        var durable = await reconstructed.ReadAsync(advisor, default);
        Assert.Equal(first.Version, durable.Version);
        Assert.Equal("Durable test data", durable.DataDurability);
        Assert.Equal(["fact-income-1"], Assert.Single(durable.Activities).SourceFactIds);
        Assert.Equal(0, (await reconstructed.ReadAsync(Advisor("T002", "A300", "C001"), default)).Version);

        var values = ConfigurationValues();
        values["Monergy:Persistence:ApplicationCase:RuntimeConnections:T002"] =
            Required("P3_APPLICATION_CASE_T001_RUNTIME");
        await using var wrong = new PostgresAdvisorEngagementApplication(
            new ConfigurationBuilder().AddInMemoryCollection(values).Build(), new FixedTimeProvider(Now));
        Assert.Equal("CASE_BINDING_INVALID", (await Assert.ThrowsAsync<ApplicationCaseException>(() =>
            wrong.ReadAsync(Advisor("T002", "A300", "C001"), default))).Code);
    }

    private static PostgresAssistanceCaseApplication Owner() => new(
        new ConfigurationBuilder().AddInMemoryCollection(ConfigurationValues()).Build(),
        new FixedTimeProvider(Now));
    private static PostgresAdvisorEngagementApplication AdvisorOwner() => new(
        new ConfigurationBuilder().AddInMemoryCollection(ConfigurationValues()).Build(),
        new FixedTimeProvider(Now));

    private static Dictionary<string, string?> ConfigurationValues() => new(StringComparer.Ordinal)
    {
        ["Monergy:ExecutionZone"] = "CI_EPHEMERAL",
        ["Monergy:ReferenceAdapters"] = "false",
        ["Monergy:Persistence:Provider"] = PhysicalPersistenceGuard.Provider,
        ["Monergy:Persistence:ApplicationCase:RuntimeConnections:T001"] = Required("P3_APPLICATION_CASE_T001_RUNTIME"),
        ["Monergy:Persistence:ApplicationCase:RuntimeConnections:T002"] = Required("P3_APPLICATION_CASE_T002_RUNTIME"),
    };

    private static CaseActorContext Customer(string tenant, string actor, string customer) =>
        new(tenant, actor, customer, 1, 1, AssistanceCaseApplication.CustomerCapability);
    private static CaseActorContext Dsa(string tenant, string actor) =>
        new(tenant, actor, null, 1, 1, AssistanceCaseApplication.DsaCapability);
    private static AdvisorEngagementContext Advisor(string tenant, string actor, string customer) =>
        new(tenant, actor, customer, 1, 1, 1, 1);
    private static string Op(char value) => new(value, 32);
    private static Func<CancellationToken, Task<CaseActorContext>> Confirm(CaseActorContext value) =>
        _ => Task.FromResult(value);
    private static Func<CancellationToken, Task<AdvisorEngagementContext>> Confirm(AdvisorEngagementContext value) =>
        _ => Task.FromResult(value);
    private static string Required(string name) => Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Physical test configuration {name} is required.");
    private static async Task<object> Attempt(Func<Task<AssistanceCaseView>> action)
    {
        try { return await action(); }
        catch (ApplicationCaseException failure) { return failure; }
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
