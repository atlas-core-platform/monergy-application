using Dapper;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.JobManagement.Application;
using Monergy.Services.JobManagement.Infrastructure;
using Npgsql;
using Xunit;

namespace Monergy.JobManagement.Tests;

public sealed class ProcessingAdministrationPhysicalTests
{
    [Fact, Trait("Category", "Physical")]
    public async Task DurableReportRetryRequiresDeployedProducerAndPreservesOriginalAuthorizationIntent()
    {
        var configuration = JobTestContext.Configuration();
        var suffix = Guid.NewGuid().ToString("N");
        var jobId = "admin-report-" + suffix;
        var customer = "report-customer-" + suffix;
        var disabled = new AdminJobScope("T001", []);
        var enabled = disabled with { AllowReportRetry = true };
        var time = JobAdministration.Time(DateTimeOffset.UtcNow);
        var security = JobTestContext.Security(customer) with { Workload = new("workspace", "local-uat-workspace:T001") };
        AdminJobReceipt receipt;
        AdminJobChange change;
        await using (var repository = new PostgresJobRepository(configuration))
        {
            await repository.ScheduleAsync(new("ScheduleJob", "1.0.0", suffix, suffix, suffix, suffix,
                null, security, jobId, "report-generation", ReportingIdentity.OwnerService,
                "private-report-intent", customer, time, time), default);
            var lease = await repository.ClaimAsync(jobId, "worker", time, TimeSpan.FromMinutes(1), default);
            var failed = await repository.FailClaimAsync(lease!, "job.reporting.owner-unavailable", true,
                time.AddHours(1), time.AddSeconds(1), default);
            change = new(Guid.NewGuid().ToString("N"), JobAdministration.Revision(failed), "retry",
                "Reviewed current customer authorization and consent");
            var denied = await Assert.ThrowsAsync<TenantBoundaryException>(() => repository.ChangeAdminAsync(
                disabled, "A900", jobId, change, time.AddSeconds(2), default));
            Assert.Equal(409, denied.Status);
            receipt = await repository.ChangeAdminAsync(enabled, "A900", jobId, change,
                time.AddSeconds(3), default);
            Assert.Equal("Financial report generation", receipt.Before.Task);
            Assert.Equal("RETRYING", receipt.After.State);
            Assert.Equal(security, (await repository.GetAsync(jobId, default))!.Security);
            Assert.Equal("private-report-intent", (await repository.GetAsync(jobId, default))!.PayloadReference);
        }
        await using var restarted = new PostgresJobRepository(configuration);
        Assert.Equal(receipt, await restarted.ReceiptAdminAsync(enabled, jobId, change.OperationId, default));
        Assert.Equal(receipt, await restarted.ChangeAdminAsync(disabled, "A900", jobId, change,
            time.AddSeconds(4), default));
        Assert.Single((await restarted.HistoryAdminAsync(enabled, jobId, null, default)).Items);
    }

    [Fact, Trait("Category", "Physical")]
    public async Task DurableRecoveryPreservesSourceTenantCustomerContextAndImmutableReceiptsAcrossRestart()
    {
        var configuration = JobTestContext.Configuration();
        var suffix = Guid.NewGuid().ToString("N");
        var jobId = "admin-processing-" + suffix;
        var customer = "admin-customer-" + suffix;
        var scope = new AdminJobScope("T001", []);
        var time = JobAdministration.Time(DateTimeOffset.UtcNow);
        var security = JobTestContext.Security(customer) with
        {
            Workload = new("workspace", "local-uat-workspace:T001"),
        };
        AdminJobReceipt receipt;
        AdminJobChange change;
        await using (var repository = new PostgresJobRepository(configuration))
        {
            await repository.ScheduleAsync(new("ScheduleJob", "1.0.0", suffix, suffix, suffix, suffix,
                null, security, jobId, "document-processing", "Document Intelligence Service",
                "private-payload", customer, time, time), default);
            var lease = await repository.ClaimAsync(jobId, "worker", time, TimeSpan.FromMinutes(1), default);
            var failed = await repository.FailClaimAsync(lease!, "job.owner.unavailable", true,
                time.AddHours(1), time.AddSeconds(1), default);
            change = new(Guid.NewGuid().ToString("N"), JobAdministration.Revision(failed), "retry",
                "Reviewed current customer permission");
            receipt = await repository.ChangeAdminAsync(scope, "A900", jobId, change,
                time.AddSeconds(2).AddTicks(7), default);
            Assert.Equal("RETRYING", receipt.After.State);
            Assert.Equal(1, receipt.After.ReplayCount);
            Assert.Null(await repository.ReadAdminAsync(new("T002", []), jobId, default));
            Assert.Null(await repository.ReadAdminAsync(new("T001", [customer]), jobId, default));
            Assert.Single((await repository.ListAdminAsync(scope, new("", "all", customer), default)).Items);
        }
        await using (var restarted = new PostgresJobRepository(configuration))
        {
            Assert.Equal(receipt, await restarted.ReceiptAdminAsync(scope, jobId, change.OperationId, default));
            Assert.Equal(receipt, await restarted.ChangeAdminAsync(scope with { AllowRetry = false },
                "A900", jobId, change, time.AddSeconds(3), default));
            Assert.Equal(security, (await restarted.GetAsync(jobId, default))!.Security);
            Assert.Single((await restarted.HistoryAdminAsync(scope, jobId, null, default)).Items);
            Assert.Single((await restarted.AttemptsAdminAsync(scope, jobId, null, default)).Items);
            var current = (await restarted.GetAsync(jobId, default))!;
            var first = new AdminJobChange(Guid.NewGuid().ToString("N"), JobAdministration.Revision(current), "cancel", "Reviewed cancellation");
            var second = first with { OperationId = Guid.NewGuid().ToString("N") };
            async Task<bool> Attempt(AdminJobChange request)
            {
                try { await restarted.ChangeAdminAsync(scope, "A900", jobId, request, time.AddSeconds(4), default); return true; }
                catch (TenantBoundaryException exception) when (exception.Status == 409) { return false; }
            }
            var results = await Task.WhenAll(Attempt(first), Attempt(second));
            Assert.Single(results, result => result);
            Assert.Equal("CANCELLED", (await restarted.ReadAdminAsync(scope, jobId, default))!.State);
            Assert.Equal(2, (await restarted.HistoryAdminAsync(scope, jobId, null, default)).Items.Count);
        }
        await using var runtime = new NpgsqlConnection(PhysicalPersistenceGuard.Require(configuration,
            "Monergy:Persistence:JobManagement:RuntimeConnection"));
        await runtime.OpenAsync();
        Assert.True(await runtime.ExecuteScalarAsync<bool>("SELECT has_table_privilege(current_user,'job_management.administrator_operations','SELECT,INSERT');"));
        Assert.False(await runtime.ExecuteScalarAsync<bool>("SELECT has_table_privilege(current_user,'job_management.administrator_operations','UPDATE,DELETE,TRUNCATE');"));
    }
}
