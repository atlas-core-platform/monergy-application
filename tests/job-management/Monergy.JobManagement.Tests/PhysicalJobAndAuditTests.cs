using System.Collections.Immutable;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.Audit.Application;
using Monergy.Services.Audit.Infrastructure;
using Monergy.Services.DocumentIntelligence.Application;
using Monergy.Services.DocumentIntelligence.Infrastructure;
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
    public async Task PendingPostgresRetryCanBeCancelledAndCannotBeClaimed()
    {
        var configuration = JobTestContext.Configuration();
        var suffix = Guid.NewGuid().ToString("N");
        var jobId = $"job-pending-retry-{suffix}";
        await using var repository = new PostgresJobRepository(configuration);
        var app = new JobManagementApplication(repository, new CapturingTelemetry(),
            new MutableTimeProvider(JobTestContext.Now));
        await app.ScheduleJobAsync(JobTestContext.Schedule(jobId, $"key-{suffix}"));
        var lease = await repository.ClaimAsync(jobId, "worker", JobTestContext.Now,
            TimeSpan.FromMinutes(1), default);
        Assert.NotNull(lease);
        await repository.FailClaimAsync(lease!, "dependency.timeout", true,
            JobTestContext.Now.AddMinutes(1), JobTestContext.Now, default);

        var cancelled = await app.CancelJobAsync(JobTestContext.Cancel(jobId));

        Assert.Equal(JobState.Cancelled, cancelled.Data?.State);
        Assert.False(cancelled.Data?.Retryable);
        Assert.Null((await repository.GetAsync(jobId, default))?.NextAttemptAt);
        Assert.Null(await repository.ClaimAsync(jobId, "other-worker", JobTestContext.Now.AddHours(1),
            TimeSpan.FromMinutes(1), default));
    }

    [Fact, Trait("Category", "Physical")]
    public async Task ClaimedPostgresCancellationFailureRequiresReconciliationWhileSuccessCompletes()
    {
        var configuration = JobTestContext.Configuration();
        var suffix = Guid.NewGuid().ToString("N");
        await using var repository = new PostgresJobRepository(configuration);
        var app = new JobManagementApplication(repository, new CapturingTelemetry(),
            new MutableTimeProvider(JobTestContext.Now));

        var failedJobId = $"job-cancel-failure-{suffix}";
        await app.ScheduleJobAsync(JobTestContext.Schedule(failedJobId, $"failure-key-{suffix}"));
        var failedLease = await repository.ClaimAsync(failedJobId, "worker-failure", JobTestContext.Now,
            TimeSpan.FromMinutes(1), default);
        Assert.NotNull(failedLease);
        await app.CancelJobAsync(JobTestContext.Cancel(failedJobId));
        var uncertain = await repository.FailClaimAsync(failedLease!, "dependency.timeout", true,
            JobTestContext.Now.AddMinutes(1), JobTestContext.Now, default);
        Assert.Equal(JobState.CancellationRequested, uncertain.State);
        Assert.False(uncertain.Retryable);
        Assert.Null(uncertain.NextAttemptAt);
        Assert.True(uncertain.ReconciliationRequired);
        Assert.Null(await repository.ClaimAsync(failedJobId, "other-worker", JobTestContext.Now.AddHours(1),
            TimeSpan.FromMinutes(1), default));
        var attemptBefore = Assert.Single(await repository.GetAttemptsAsync(failedJobId, default));
        Assert.Equal("CANCELLATION_OUTCOME_UNKNOWN", attemptBefore.Outcome);
        await repository.ReconcileAsync(failedJobId, JobTestContext.CustomerId,
            JobReconciliationOutcome.ConfirmedNoEffect, JobTestContext.Now.AddMinutes(2), default);
        Assert.Equal(attemptBefore, Assert.Single(await repository.GetAttemptsAsync(failedJobId, default)));

        var completedJobId = $"job-cancel-success-{suffix}";
        await app.ScheduleJobAsync(JobTestContext.Schedule(completedJobId, $"success-key-{suffix}"));
        var completedLease = await repository.ClaimAsync(completedJobId, "worker-success", JobTestContext.Now,
            TimeSpan.FromMinutes(1), default);
        Assert.NotNull(completedLease);
        await app.CancelJobAsync(JobTestContext.Cancel(completedJobId));
        var completed = await repository.CompleteClaimAsync(completedLease!, "known-domain-effect",
            JobTestContext.Now.AddSeconds(1), default);
        Assert.Equal(JobState.Completed, completed.State);
        Assert.Equal("known-domain-effect", completed.OutcomeReference);
    }

    [Fact, Trait("Category", "Physical")]
    public async Task GovernedProcessDocumentJobSurvivesReconstructionExecutesAndDispatchesAuditEvidence()
    {
        var configuration = JobTestContext.Configuration();
        var suffix = Guid.NewGuid().ToString("N");
        var jobId = $"job-document-{suffix}";
        var processingId = $"processing-{suffix}";
        var clock = new MutableTimeProvider(JobTestContext.Now);
        await using (var initial = new PostgresJobRepository(configuration))
        {
            var initialApp = new JobManagementApplication(initial, new CapturingTelemetry(), clock);
            Assert.Equal(ContractOutcome.Success,
                (await initialApp.ScheduleJobAsync(JobTestContext.Schedule(jobId, $"key-{suffix}", processingId))).Outcome);
            Assert.Equal(JobState.Scheduled,
                (await initialApp.GetJobStatusAsync(JobTestContext.Status(jobId))).Data?.State);
        }

        await using var jobs = new PostgresJobRepository(configuration);
        var app = new JobManagementApplication(jobs, new CapturingTelemetry(), clock);
        var documentRepository = new InMemoryDocumentProcessingRepository();
        var documentApp = new DocumentProcessingApplication(documentRepository, new FixtureEvidenceContentReader(),
            new FixtureDocumentExtractor(), new ReferenceExecutionPolicy(), new CapturingTelemetry(), clock);
        var registry = new ReferenceJobExecutionTargetRegistry();
        registry.Register("Document Intelligence Service", Vs02ContractNames.ProcessDocument,
            new DocumentProcessingJobTarget(documentApp));
        var policy = new ReferenceJobExecutionPolicy(configuration, clock);
        policy.GrantAuthorization("authorization-d10");
        policy.SetConsent(new("consent-d10", JobTestContext.CustomerId, "DURABLE_PROCESSING",
            clock.Now.AddHours(1), false));
        var worker = new DurableJobExecutionWorker(configuration, jobs, app, policy, policy, registry, clock,
            NullLogger<DurableJobExecutionWorker>.Instance);

        Assert.True(await worker.RunJobOnceAsync(jobId));
        Assert.Equal(JobState.Completed, (await jobs.GetAsync(jobId, default))?.State);
        Assert.Equal(ProcessingState.Completed, (await documentRepository.GetAsync(processingId, default))?.State);
        Assert.Equal(1, policy.AuthorizationEvaluations);
        Assert.Equal(1, policy.ConsentEvaluations);

        await using var auditRepository = new PostgresAuditEvidenceRepository(configuration);
        var audit = new AuditApplication(auditRepository, new CapturingTelemetry(), clock);
        var deliveryTelemetry = new EventDeliveryTelemetry();
        var transport = new ReferenceGovernedEventTransport<AuditableEvent>(configuration,
            [new AuditTransportConsumer(audit)], deliveryTelemetry);
        var dispatch = new JobOutboxDispatchWorker(configuration,
            new JobOutboxDispatcher(jobs, transport, deliveryTelemetry, clock), clock,
            NullLogger<JobOutboxDispatchWorker>.Instance);
        Assert.True(await dispatch.RunOnceAsync() >= 2);
        Assert.Contains(await audit.ReadAllAsync(), item => item.SubjectId == jobId &&
            item.EventName == Vs02ContractNames.JobCompleted);
    }

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
    public async Task RetryStateAndClaimLeaseSurviveRepositoryReconstruction()
    {
        var configuration = JobTestContext.Configuration();
        var suffix = Guid.NewGuid().ToString("N");
        await using var first = new PostgresJobRepository(configuration);
        var app = new JobManagementApplication(first, new CapturingTelemetry(), new MutableTimeProvider(JobTestContext.Now));
        await app.ScheduleJobAsync(JobTestContext.Schedule($"job-{suffix}", $"key-{suffix}"));
        var policy = new ReferenceJobExecutionPolicy(configuration, new MutableTimeProvider(JobTestContext.Now));
        policy.GrantAuthorization("authorization-d10");
        policy.SetConsent(new("consent-d10", JobTestContext.CustomerId, "DURABLE_PROCESSING",
            JobTestContext.Now.AddHours(1), false));
        Assert.Equal(ContractOutcome.Failed,
            (await app.ExecuteAsync($"job-{suffix}", policy, policy,
                new CountingTarget(JobExecutionResult.Failed("dependency.timeout.unknown", true)))).Outcome);

        await using var reconstructed = new PostgresJobRepository(configuration);
        var claims = await Task.WhenAll(
            reconstructed.ClaimAsync($"job-{suffix}", "worker-a", JobTestContext.Now.AddMinutes(2),
                TimeSpan.FromMinutes(1), default),
            reconstructed.ClaimAsync($"job-{suffix}", "worker-b", JobTestContext.Now.AddMinutes(2),
                TimeSpan.FromMinutes(1), default));
        var lease = Assert.Single(claims, claim => claim is not null)!;
        Assert.Equal(1, lease.Job.RetryCount);
        Assert.Equal(0, lease.Job.ReplayCount);
        Assert.Equal(1, await reconstructed.RecoverInterruptedAsync(JobTestContext.Now.AddMinutes(4), default));
        var uncertain = await reconstructed.GetAsync($"job-{suffix}", default);
        Assert.True(uncertain?.ReconciliationRequired);
        Assert.Equal(JobState.Failed, uncertain?.State);
        var attemptsBefore = await reconstructed.GetAttemptsAsync($"job-{suffix}", default);
        Assert.Equal(2, attemptsBefore.Count);
        var attemptBefore = attemptsBefore[1];
        Assert.Equal("OUTCOME_UNKNOWN", attemptBefore.Outcome);
        Assert.Equal("job.execution.outcome-unknown", attemptBefore.FailureCode);
        Assert.NotNull(attemptBefore.EndedAt);
        var recovered = await reconstructed.ReconcileAsync($"job-{suffix}", JobTestContext.CustomerId,
            JobReconciliationOutcome.ConfirmedNoEffect, JobTestContext.Now.AddMinutes(5), default);
        Assert.Equal(JobState.Scheduled, recovered.State);
        Assert.Equal(1, recovered.RetryCount);
        Assert.Equal(0, recovered.ReplayCount);
        var attemptsAfter = await reconstructed.GetAttemptsAsync($"job-{suffix}", default);
        Assert.Equal(2, attemptsAfter.Count);
        Assert.Equal(attemptBefore, attemptsAfter[1]);
    }

    [Fact, Trait("Category", "Physical")]
    public async Task CancellationRequestedRestartRequiresExplicitReconciliation()
    {
        var configuration = JobTestContext.Configuration();
        var suffix = Guid.NewGuid().ToString("N");
        var jobId = $"job-cancel-recovery-{suffix}";
        await using var repository = new PostgresJobRepository(configuration);
        var app = new JobManagementApplication(repository, new CapturingTelemetry(),
            new MutableTimeProvider(JobTestContext.Now));
        await app.ScheduleJobAsync(JobTestContext.Schedule(jobId, $"key-{suffix}"));
        Assert.NotNull(await repository.ClaimAsync(jobId, "worker-cancel", JobTestContext.Now,
            TimeSpan.FromMinutes(1), default));
        Assert.Equal(JobState.CancellationRequested,
            (await app.CancelJobAsync(JobTestContext.Cancel(jobId))).Data?.State);

        Assert.Equal(1, await repository.RecoverInterruptedAsync(JobTestContext.Now.AddMinutes(2), default));
        var uncertain = await repository.GetAsync(jobId, default);
        Assert.Equal(JobState.CancellationRequested, uncertain?.State);
        Assert.True(uncertain?.ReconciliationRequired);
        var attemptBefore = Assert.Single(await repository.GetAttemptsAsync(jobId, default));
        Assert.Equal("CANCELLATION_OUTCOME_UNKNOWN", attemptBefore.Outcome);
        Assert.Equal("job.cancellation.outcome-unknown", attemptBefore.FailureCode);
        Assert.NotNull(attemptBefore.EndedAt);
        var cancelled = await repository.ReconcileAsync(jobId, JobTestContext.CustomerId,
            JobReconciliationOutcome.ConfirmedCancelled, JobTestContext.Now.AddMinutes(3), default);
        Assert.Equal(JobState.Cancelled, cancelled.State);
        Assert.Equal(attemptBefore, Assert.Single(await repository.GetAttemptsAsync(jobId, default)));
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
