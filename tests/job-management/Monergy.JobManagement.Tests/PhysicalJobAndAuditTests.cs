using System.Collections.Immutable;
using Dapper;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.Audit.Application;
using Monergy.Services.Audit.Infrastructure;
using Monergy.Services.JobManagement.Application;
using Monergy.Services.JobManagement.Infrastructure;
using Monergy.Services.Reporting.Application;
using Monergy.Services.Reporting.Infrastructure;
using Npgsql;
using Xunit;

namespace Monergy.JobManagement.Tests;

public sealed class PhysicalJobAndAuditTests
{
    [Fact, Trait("Category", "Physical")]
    public async Task PersistedJobSurvivesRepositoryReconstructionAndDatabaseRestart()
    {
        var configuration = JobTestContext.Configuration();
        var app = new JobManagementApplication(new PostgresJobRepository(configuration),
            new CapturingTelemetry(), new MutableTimeProvider(JobTestContext.Now));
        var scheduled = await app.ScheduleJobAsync(JobTestContext.Schedule("d10-retained-job", "d10-retained-key"));
        Assert.Equal(ContractOutcome.Success, scheduled.Outcome);

        await using var reconstructed = new PostgresJobRepository(configuration);
        var durable = await reconstructed.GetAsync("d10-retained-job", default);
        Assert.NotNull(durable);
        Assert.Equal(JobTestContext.CustomerId, durable.CustomerId);
    }

    [Fact, Trait("Category", "Physical")]
    public async Task RetryCancellationReplayAndInterruptedRecoveryPersistAcrossRepositories()
    {
        var configuration = JobTestContext.Configuration();
        var suffix = Guid.NewGuid().ToString("N");
        await using var first = new PostgresJobRepository(configuration);
        var app = new JobManagementApplication(first, new CapturingTelemetry(), new MutableTimeProvider(JobTestContext.Now));
        await app.ScheduleJobAsync(JobTestContext.Schedule($"job-{suffix}", $"key-{suffix}"));
        await app.StartAsync($"job-{suffix}");

        await using var reconstructed = new PostgresJobRepository(configuration);
        Assert.Equal(1, await reconstructed.RecoverInterruptedAsync(JobTestContext.Now.AddMinutes(1), default));
        var recovered = await reconstructed.GetAsync($"job-{suffix}", default);
        Assert.Equal(JobState.Scheduled, recovered?.State);
        await reconstructed.StartAsync($"job-{suffix}", JobTestContext.Now.AddMinutes(2), default);
        await reconstructed.FailAsync($"job-{suffix}", "dependency.timeout.unknown", true,
            JobTestContext.Now.AddMinutes(3), JobTestContext.Now.AddMinutes(2), default);
        Assert.Equal(JobState.Scheduled, (await reconstructed.ReplayAsync($"job-{suffix}",
            JobTestContext.CustomerId, JobTestContext.Now.AddMinutes(3), default)).State);
        Assert.Equal(JobState.Cancelled, (await reconstructed.CancelAsync($"job-{suffix}",
            JobTestContext.CustomerId, "customer-requested", JobTestContext.Now.AddMinutes(4), default)).State);
    }

    [Fact, Trait("Category", "Physical")]
    public async Task JobOutboxSurvivesDispatcherFailureAndAuditDeduplicatesRedelivery()
    {
        var configuration = JobTestContext.Configuration();
        var suffix = Guid.NewGuid().ToString("N");
        await using var jobs = new PostgresJobRepository(configuration);
        var app = new JobManagementApplication(jobs, new CapturingTelemetry(), new MutableTimeProvider(JobTestContext.Now));
        await app.ScheduleJobAsync(JobTestContext.Schedule($"job-dispatch-{suffix}", $"key-dispatch-{suffix}"));
        await app.StartAsync($"job-dispatch-{suffix}");
        foreach (var unrelated in (await jobs.PendingEventsAsync(default))
                     .Where(item => item.SubjectId != $"job-dispatch-{suffix}"))
            await jobs.MarkDispatchedAsync(unrelated.EventId, JobTestContext.Now, default);

        await using var auditRepository = new PostgresAuditEvidenceRepository(configuration);
        var audit = new AuditApplication(auditRepository, new CapturingTelemetry(), new MutableTimeProvider(JobTestContext.Now));
        var deliveryTelemetry = new EventDeliveryTelemetry();
        var transport = new ReferenceGovernedEventTransport<AuditableEvent>(configuration, [new AuditTransportConsumer(audit)], deliveryTelemetry);
        var dispatcher = new JobOutboxDispatcher(jobs, transport, deliveryTelemetry, new MutableTimeProvider(JobTestContext.Now))
        {
            FailAfterPublishBeforeMarkerOnce = true
        };

        await Assert.ThrowsAsync<IOException>(() => dispatcher.DispatchAsync());
        Assert.Single(await jobs.PendingEventsAsync(default),
            item => item.SubjectId == $"job-dispatch-{suffix}");
        var afterCrash = (await audit.ReadAllAsync()).Where(item => item.SubjectId == $"job-dispatch-{suffix}").ToArray();
        Assert.Single(afterCrash);
        Assert.Equal(1, await dispatcher.DispatchAsync());
        Assert.DoesNotContain(await jobs.PendingEventsAsync(default),
            item => item.SubjectId == $"job-dispatch-{suffix}");
        Assert.Single(await audit.ReadAllAsync(), item => item.SubjectId == $"job-dispatch-{suffix}");
        Assert.Equal(new(2, 1, 1, 0), deliveryTelemetry.Snapshot());
    }

    [Fact, Trait("Category", "Physical")]
    public async Task ExistingReportingOutboxPropagatesCid053IntoAppendOnlyAuditExactlyOnce()
    {
        var configuration = JobTestContext.Configuration();
        var suffix = Guid.NewGuid().ToString("N");
        await using var reports = new PostgresReportRepository(configuration);
        var identity = new ReportOperationIdentity(ReportingContractNames.GenerateReport,
            ContractGuard.CurrentVersion, JobTestContext.CustomerId, $"report-key-{suffix}");
        var generated = JobTestContext.Now;
        var operation = await reports.GetOrCreateAsync(identity, _ => Task.FromResult(
            ReportGenerationAttempt.Succeeded(new($"report-{suffix}", JobTestContext.CustomerId,
                "GENERATED", generated, ImmutableArray<ReportLineItem>.Empty, [], [], [], [], null,
                $"audit-{suffix}", new($"report-{suffix}.json", "application/json", "{}", new string('A', 64))))),
            report => new("CID-053", $"report-event-{suffix}", ReportingContractNames.ReportGenerated,
                ContractGuard.CurrentVersion, generated, $"report-correlation-{suffix}", null,
                "Reporting Service", "Report", report.ReportId,
                new(report.ReportId, report.CustomerId, report.State, report.SourceFinancialReferences,
                    report.EvidenceReferences, report.FinancialProvenanceReferences,
                    report.CalculationLineageReferences, report.AiResponseTraceReference,
                    report.AuditCompatibilityReferenceId, report.Export.Sha256)));
        Assert.True(operation.Created);

        await using var auditRepository = new PostgresAuditEvidenceRepository(configuration);
        var audit = new AuditApplication(auditRepository, new CapturingTelemetry(), new MutableTimeProvider(generated));
        var deliveryTelemetry = new EventDeliveryTelemetry();
        var transport = new ReferenceGovernedEventTransport<AuditableEvent>(configuration, [new AuditTransportConsumer(audit)], deliveryTelemetry);
        var dispatcher = new ReportingAuditOutboxDispatcher(reports, transport, deliveryTelemetry)
        {
            FailAfterPublishBeforeMarkerOnce = true
        };
        await Assert.ThrowsAsync<IOException>(() => dispatcher.DispatchAsync());
        Assert.Single(reports.PendingEvents(), item => item.EventId == $"report-event-{suffix}");
        Assert.Equal(1, await dispatcher.DispatchAsync());
        Assert.DoesNotContain(reports.PendingEvents(), item => item.EventId == $"report-event-{suffix}");
        Assert.Single(await audit.ReadAllAsync(), item => item.SourceEventId == $"report-event-{suffix}");
    }

    [Fact, Trait("Category", "Physical")]
    public async Task RuntimeRolesEnforceDatabaseDdlOutboxAndAuditLeastPrivilege()
    {
        await using var jobs = new NpgsqlConnection(Required("D10_JOB_MANAGEMENT_RUNTIME_CONNECTION"));
        await jobs.OpenAsync();
        Assert.False(await jobs.ExecuteScalarAsync<bool>(
            "SELECT has_table_privilege(current_user, 'job_management.outbox', 'UPDATE');"));
        Assert.True(await jobs.ExecuteScalarAsync<bool>(
            "SELECT has_column_privilege(current_user, 'job_management.outbox', 'dispatched_at', 'UPDATE');"));
        await Assert.ThrowsAnyAsync<PostgresException>(() => jobs.ExecuteAsync(
            "CREATE TABLE job_management.forbidden(id integer);"));
        await Assert.ThrowsAnyAsync<NpgsqlException>(async () =>
        {
            await using var denied = new NpgsqlConnection(WithDatabase(
                Required("D10_JOB_MANAGEMENT_RUNTIME_CONNECTION"), "monergy_audit"));
            await denied.OpenAsync();
        });

        await using var audit = new NpgsqlConnection(Required("D10_AUDIT_RUNTIME_CONNECTION"));
        await audit.OpenAsync();
        await Assert.ThrowsAnyAsync<PostgresException>(() => audit.ExecuteAsync("DELETE FROM audit.evidence;"));
        await Assert.ThrowsAnyAsync<PostgresException>(() => audit.ExecuteAsync("UPDATE audit.evidence SET event_name='forbidden';"));
    }

    [Fact, Trait("Category", "Physical")]
    public async Task OperationalMetricsExposeDurableLifecycleAndBacklogWithoutThresholdClaims()
    {
        var configuration = JobTestContext.Configuration();
        var suffix = Guid.NewGuid().ToString("N");
        await using var repository = new PostgresJobRepository(configuration);
        var app = new JobManagementApplication(repository, new CapturingTelemetry(), new MutableTimeProvider(JobTestContext.Now));
        await app.ScheduleJobAsync(JobTestContext.Schedule($"job-metrics-{suffix}", $"key-metrics-{suffix}"));
        await app.StartAsync($"job-metrics-{suffix}");
        await app.FailAsync($"job-metrics-{suffix}", "transient", true);

        var snapshot = await repository.GetOperationalSnapshotAsync(JobTestContext.Now.AddMinutes(5), default);
        Assert.True(snapshot.Failed >= 1);
        Assert.True(snapshot.OutboxBacklog >= 2);
    }

    private static string Required(string name) => Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Required D10 environment '{name}' is unavailable.");

    private static string WithDatabase(string connection, string database)
    {
        var builder = new NpgsqlConnectionStringBuilder(connection) { Database = database };
        return builder.ConnectionString;
    }
}
