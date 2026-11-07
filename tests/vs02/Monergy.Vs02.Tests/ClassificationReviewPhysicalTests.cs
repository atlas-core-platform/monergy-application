using Microsoft.Extensions.Configuration;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.DocumentIntelligence.Application;
using Monergy.Services.DocumentIntelligence.Infrastructure;
using Npgsql;
using Xunit;
namespace Monergy.Vs02.Tests;
public sealed class ClassificationReviewPhysicalTests
{
    [Fact, Trait("Category", "Physical")]
    public async Task PhysicalReviewsSurviveAdapterRestartAndRejectStaleWriters()
    {
        var connection = Environment.GetEnvironmentVariable("T111_CLASSIFICATION_RUNTIME_CONNECTION") ?? throw new InvalidOperationException("Supply a migrated isolated test database runtime connection.");
        var config = new ConfigurationManager { ["Monergy:ExecutionZone"] = "CI_EPHEMERAL", ["Monergy:Classification:RuntimeConnections:T001"] = connection };
        var suffix = Guid.NewGuid().ToString("N"); var row = new DocumentClassification("T001", "customer-" + suffix, "version-" + suffix, new('a', 64), ClassificationRules.Version, "UNCLASSIFIED", "NO_TEXT", DateTimeOffset.UtcNow, 1, []);
        await using (var first = new PostgresDocumentClassificationRepository(config))
        {
            await first.RecordAsync(row, default);
            await first.SaveAsync(new(row.TenantId, row.CustomerId, row.DocumentVersionId, row.SourceHash, DocumentOcrRules.Version, "AVAILABLE", DateTimeOffset.UtcNow, [new(1, [new(1, "Retained OCR text", 99)])]), default);
            await first.ReviewAsync(row.TenantId, row.CustomerId, row.DocumentVersionId, "A900", new(1, "OTHER", "OTHER_DOCUMENT"), DateTimeOffset.UtcNow, default);
        }
        await using (var restarted = new PostgresDocumentClassificationRepository(config))
        {
            var retained = await restarted.ReadAsync(row.TenantId, row.CustomerId, row.DocumentVersionId, default);
            var ocr = await restarted.ReadOcrAsync(row.TenantId, row.CustomerId, row.DocumentVersionId, default);
            Assert.Equal("Retained OCR text", ocr!.Pages[0].Lines[0].Text); Assert.Equal(row.SourceHash, ocr.SourceHash);
            await Assert.ThrowsAsync<EvidenceDependencyException>(() => restarted.SaveAsync(ocr with { SourceHash = new('b', 64) }, default));
            Assert.Equal(2, retained!.Revision); Assert.Single(retained.Reviews); Assert.Equal("OTHER", retained.DocumentClass);
            Assert.Equal(retained.SourceHash, (await restarted.RecordAsync(row, default)).SourceHash);
            var error = await Assert.ThrowsAsync<TenantBoundaryException>(() => restarted.ReviewAsync(row.TenantId, row.CustomerId, row.DocumentVersionId, "A901", new(1, "PAN", "SOURCE_REVIEWED"), DateTimeOffset.UtcNow, default)); Assert.Equal(409, error.Status);
        }
        await using var db = new NpgsqlConnection(connection); await db.OpenAsync();
        await using var grants = new NpgsqlCommand("SELECT has_table_privilege(current_user,'document_classifications','DELETE,TRUNCATE') OR has_table_privilege(current_user,'document_ocr','DELETE,TRUNCATE')", db);
        Assert.False((bool)(await grants.ExecuteScalarAsync())!);
    }

    [Fact, Trait("Category", "Physical")]
    public async Task CompletedProcessingResultAndTerminalEventSurviveAdapterRestart()
    {
        var connection = Environment.GetEnvironmentVariable("T111_CLASSIFICATION_RUNTIME_CONNECTION")
            ?? throw new InvalidOperationException("Supply a migrated isolated test database runtime connection.");
        var config = new ConfigurationManager
        {
            ["Monergy:ExecutionZone"] = "CI_EPHEMERAL",
            ["Monergy:Classification:RuntimeConnections:T001"] = connection,
        };
        var suffix = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        var processingId = "processing-" + suffix;
        var customerId = "customer-" + suffix;
        var versionId = "version-" + suffix;
        var candidate = new ProcessingRecord(
            processingId, versionId, "evidence-" + suffix, customerId,
            Vs02ContractNames.ProcessDocument, ContractGuard.CurrentVersion,
            "idempotency-" + suffix, new string('a', 64), null, null, null,
            ProcessingState.Accepted, null, null, false, 0, null, null, now, [], "T001");
        var fact = new ValidatedSourceFact(
            "source-fact-" + suffix, customerId, "INCOME", "Net pay", 52480m, "INR",
            new DateOnly(2026, 9, 30), "Salary Slip OCR: page 1, line 4", 0.92m,
            candidate.EvidenceReferenceId, versionId, "salary-slip-png-ocr/1.0.0",
            "canonical-source-fact/1.0.0");
        var terminal = new DomainEvent<ValidatedSourceFactsProducedPayload>(
            "CID-028", "evt-" + suffix, Vs02ContractNames.ValidatedSourceFactsProduced,
            ContractGuard.CurrentVersion, now.AddSeconds(1), "correlation-" + suffix,
            "request-" + suffix, "Document Intelligence Service", "document-processing",
            processingId, new(processingId, customerId, candidate.EvidenceReferenceId, versionId, [fact]));

        await using (var first = new PostgresDocumentClassificationRepository(config))
        {
            Assert.True((await first.AcceptAsync(candidate, default)).Created);
            var running = await first.StartAttemptAsync(
                processingId, ProcessingState.Accepted, now.AddMilliseconds(1), default);
            var completed = await first.CommitTerminalAsync(
                processingId, running.ActiveAttemptId!, ProcessingState.Completed,
                terminal.OccurredAt, [fact], null, null, false, terminal, default);
            Assert.Equal(terminal.EventId, completed.TerminalEventId);
        }

        await using (var restarted = new PostgresDocumentClassificationRepository(config))
        {
            var retained = await restarted.GetAsync(processingId, default);
            Assert.NotNull(retained);
            Assert.Equal(ProcessingState.Completed, retained.State);
            Assert.Equal(fact, Assert.Single(retained.Facts));
            Assert.False((await restarted.AcceptAsync(candidate, default)).Created);
            var monitor = await restarted.MonitorAsync("T001", customerId, versionId, null, default);
            Assert.Equal(processingId, Assert.Single(monitor.Items).ProcessingId);
        }

        await using var db = new NpgsqlConnection(connection);
        await db.OpenAsync();
        await using var grants = new NpgsqlCommand(
            "SELECT " +
            "has_table_privilege(current_user,'document_processing','SELECT,INSERT,UPDATE') " +
            "AND NOT has_table_privilege(current_user,'document_processing','DELETE,TRUNCATE') " +
            "AND has_table_privilege(current_user,'document_processing_events','SELECT,INSERT') " +
            "AND NOT has_table_privilege(current_user,'document_processing_events','UPDATE,DELETE,TRUNCATE')",
            db);
        Assert.True((bool)(await grants.ExecuteScalarAsync())!);
    }
}
