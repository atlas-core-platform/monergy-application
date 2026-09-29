using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Configuration;
using Monergy.Contracts;
using Monergy.Services.Audit.Infrastructure;
using Monergy.Services.Evidence.Application;
using Monergy.Services.Evidence.Infrastructure;
using Monergy.Services.FinancialProfile.Application;
using Monergy.Services.FinancialProfile.Infrastructure;
using Monergy.Services.FinancialRules.Application;
using Monergy.Services.FinancialRules.Infrastructure;
using Monergy.Services.Reporting.Application;
using Monergy.Services.Reporting.Infrastructure;
using Npgsql;
using Xunit;

namespace Monergy.Persistence.Tests;

public sealed class PersistentFoundationTests
{
    [Fact]
    public async Task FiveServiceSchemasAndMigrationJournalsExist()
    {
        foreach (var service in Services)
        {
            await using var connection = new NpgsqlConnection(Owner(service));
            await connection.OpenAsync();
            var schema = service.Replace('-', '_');
            Assert.True(await connection.ExecuteScalarAsync<bool>(
                "SELECT EXISTS (SELECT 1 FROM information_schema.schemata WHERE schema_name=@schema);", new { schema }));
            Assert.True(await connection.ExecuteScalarAsync<bool>(
                "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema='public' AND table_name='monergy_migration_history');"));
        }
    }

    [Fact]
    public async Task RuntimeRolesAreDatabaseAndDdlIsolated()
    {
        foreach (var access in new[]
        {
            (Connection: Runtime("evidence"), Database: "monergy_financial_profile"),
            (Connection: Runtime("financial-profile"), Database: "monergy_evidence"),
            (Connection: Runtime("financial-rules"), Database: "monergy_financial_profile"),
            (Connection: Runtime("reporting"), Database: "monergy_financial_profile"),
            (Connection: Runtime("reporting"), Database: "monergy_financial_rules"),
            (Connection: Owner("evidence"), Database: "monergy_financial_profile"),
        })
        {
            await Assert.ThrowsAnyAsync<NpgsqlException>(async () =>
            {
                await using var denied = new NpgsqlConnection(WithDatabase(access.Connection, access.Database));
                await denied.OpenAsync();
            });
        }
        await using var runtime = new NpgsqlConnection(Runtime("financial-rules"));
        await runtime.OpenAsync();
        await Assert.ThrowsAnyAsync<PostgresException>(() => runtime.ExecuteAsync("CREATE TABLE financial_rules.forbidden(id integer);"));
        await Assert.ThrowsAnyAsync<PostgresException>(() => runtime.ExecuteAsync(
            "ALTER TABLE financial_rules.calculations ADD COLUMN forbidden text;"));
        await Assert.ThrowsAnyAsync<PostgresException>(() => runtime.ExecuteAsync(
            "DROP TABLE financial_rules.calculations;"));
    }

    [Fact]
    public async Task EvidenceMetadataOutboxAndS3BytesSurviveAdapterReconstruction()
    {
        var configuration = Configuration("Evidence");
        string reference;
        byte[] expected;
        var retainedPath = Environment.GetEnvironmentVariable("D09_EVIDENCE_REFERENCE_FILE");
        if (!string.IsNullOrWhiteSpace(retainedPath) && File.Exists(retainedPath))
        {
            var retained = JsonSerializer.Deserialize<RetainedContent>(await File.ReadAllTextAsync(retainedPath))!;
            reference = retained.Reference;
            expected = Convert.FromBase64String(retained.Content);
            using var retainedStore = new S3EvidenceContentStore(configuration);
            Assert.Equal(expected, await retainedStore.ReadAsync(reference));
        }

        expected = Encoding.UTF8.GetBytes($"synthetic-evidence-{Guid.NewGuid():N}");
        using var store = new S3EvidenceContentStore(configuration);
        var staged = await store.StageAsync(new MemoryStream(expected), "text/plain");
        reference = staged.Descriptor.Reference;
        Assert.DoesNotContain("customer", staged.ObjectKey, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("statement.txt", staged.ObjectKey, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(staged.ObjectVersionId));
        Assert.Equal(expected.LongLength, staged.Descriptor.ContentLength);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(expected)).ToLowerInvariant(), staged.Descriptor.Sha256);
        Assert.Equal(expected, await store.ReadAsync(reference));

        var suffix = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        var candidate = new EvidenceVersionRecord($"doc-{suffix}", $"doc-version-{suffix}",
            $"evidence-{suffix}", $"customer-{suffix}", 0, "synthetic-fixture", "statement.txt",
            "text/plain", reference, staged.Descriptor.Sha256, now, now, expected.LongLength,
            staged.ObjectKey, staged.ObjectVersionId);
        var identity = new EvidenceOperationIdentity("CreateDocumentVersion", "1.0.0", candidate.CustomerId, $"key-{suffix}");
        await using (var repository = new PostgresEvidenceRepository(configuration))
        {
            var saved = await repository.SaveVersionAsync(identity, candidate,
                value => [Event("CID-023", $"evt-{suffix}", value.DocumentVersionId, now)], default);
            Assert.True(saved.Created);
        }
        await using (var reconstructed = new PostgresEvidenceRepository(configuration))
        {
            var replay = await reconstructed.SaveVersionAsync(identity, candidate,
                value => [Event("CID-023", $"evt-duplicate-{suffix}", value.DocumentVersionId, now)], default);
            Assert.False(replay.Created);
            Assert.NotNull(await reconstructed.GetVersionAsync(candidate.DocumentVersionId, default));
        }
        await using var sql = new NpgsqlConnection(Runtime("evidence"));
        Assert.Equal(1, await sql.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM evidence.outbox WHERE subject_id=@Id;", new { Id = candidate.DocumentVersionId }));
        if (!string.IsNullOrWhiteSpace(retainedPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(retainedPath)!);
            await File.WriteAllTextAsync(retainedPath, JsonSerializer.Serialize(new RetainedContent(reference,
                Convert.ToBase64String(expected))));
        }
    }

    [Fact]
    public async Task ReportingCommitIdempotencyAndEventAreAtomicAndDurable()
    {
        var configuration = Configuration("Reporting");
        await using var repository = new PostgresReportRepository(configuration);
        var suffix = Guid.NewGuid().ToString("N");
        var identity = new ReportOperationIdentity("GenerateReport", "1.0.0", $"customer-{suffix}", $"key-{suffix}");
        var generated = DateTimeOffset.UtcNow;
        var reads = 0;
        Task<ReportGenerationAttempt> Factory(CancellationToken _)
        {
            reads++;
            var report = new TrustedFinancialReport($"report-{suffix}", identity.CustomerId, "GENERATED", generated,
                ImmutableArray<ReportLineItem>.Empty, [], [], [], [], null, $"audit-{suffix}",
                new($"report-{suffix}.json", "application/json", "{}", new string('A', 64)));
            return Task.FromResult(ReportGenerationAttempt.Succeeded(report));
        }
        DomainEvent<ReportGeneratedPayload> EventFactory(TrustedFinancialReport report) => new("CID-053",
            $"event-{report.ReportId}", "ReportGenerated", "1.0.0", report.GeneratedAt, $"correlation-{suffix}", null,
            "Reporting Service", "Report", report.ReportId, new(report.ReportId, report.CustomerId, report.State,
                report.SourceFinancialReferences, report.EvidenceReferences, report.FinancialProvenanceReferences,
                report.CalculationLineageReferences, null, report.AuditCompatibilityReferenceId, report.Export.Sha256));
        var first = await repository.GetOrCreateAsync(identity, Factory, EventFactory);
        var replay = await repository.GetOrCreateAsync(identity, Factory, EventFactory);
        Assert.True(first.Created);
        Assert.False(replay.Created);
        Assert.Equal(1, reads);
        Assert.Equal(first.Report!.ReportId, replay.Report!.ReportId);
        Assert.Equal(first.Report.GeneratedAt, replay.Report.GeneratedAt);
        Assert.Equal(first.Report.Export, replay.Report.Export);
        Assert.Single(repository.PendingEvents(), item => item.SubjectId == first.Report!.ReportId);
        await using var reconstructed = new PostgresReportRepository(configuration);
        var durable = reconstructed.Find(identity.CustomerId, first.Report.ReportId);
        Assert.NotNull(durable);
        Assert.Equal(first.Report.ReportId, durable.ReportId);
        Assert.Equal(first.Report.GeneratedAt, durable.GeneratedAt);
        Assert.Equal(first.Report.Export, durable.Export);
        Assert.Null(reconstructed.Find($"other-customer-{suffix}", first.Report.ReportId));

        var nextIdentity = identity with { IdempotencyKey = $"new-key-{suffix}" };
        var next = await reconstructed.GetOrCreateAsync(nextIdentity, _ => Task.FromResult(
            ReportGenerationAttempt.Succeeded(first.Report with
            {
                ReportId = $"report-new-{suffix}",
                Export = first.Report.Export with { FileName = $"report-new-{suffix}.json" }
            })), EventFactory);
        Assert.True(next.Created);
        Assert.NotEqual(first.Report.ReportId, next.Report!.ReportId);

        var concurrentIdentity = identity with { IdempotencyKey = $"concurrent-key-{suffix}" };
        var concurrentReads = 0;
        Task<ReportGenerationAttempt> ConcurrentFactory(CancellationToken _)
        {
            Interlocked.Increment(ref concurrentReads);
            return Task.FromResult(ReportGenerationAttempt.Succeeded(first.Report with
            {
                ReportId = $"report-concurrent-{suffix}",
                Export = first.Report.Export with { FileName = $"report-concurrent-{suffix}.json" }
            }));
        }
        await using var concurrentRepository = new PostgresReportRepository(configuration);
        var concurrent = await Task.WhenAll(
            reconstructed.GetOrCreateAsync(concurrentIdentity, ConcurrentFactory, EventFactory),
            concurrentRepository.GetOrCreateAsync(concurrentIdentity, ConcurrentFactory, EventFactory));
        Assert.Equal(1, concurrentReads);
        Assert.Single(concurrent.Select(item => item.Report!.ReportId).Distinct());
    }

    [Fact]
    public async Task EvidenceStateAndOutboxRollbackTogetherAndRetryCanSucceed()
    {
        var configuration = Configuration("Evidence");
        var suffix = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        var first = EvidenceCandidate(suffix, "first", now);
        var failed = EvidenceCandidate(suffix, "failed", now);
        var firstIdentity = new EvidenceOperationIdentity("CreateDocumentVersion", "1.0.0", first.CustomerId,
            $"key-first-{suffix}");
        var failedIdentity = new EvidenceOperationIdentity("CreateDocumentVersion", "1.0.0", failed.CustomerId,
            $"key-failed-{suffix}");
        var duplicateEventId = $"event-rollback-{suffix}";

        await using var repository = new PostgresEvidenceRepository(configuration);
        await repository.SaveVersionAsync(firstIdentity, first,
            value => [Event("CID-023", duplicateEventId, value.DocumentVersionId, now)], default);
        await Assert.ThrowsAnyAsync<PostgresException>(() => repository.SaveVersionAsync(failedIdentity, failed,
            value => [Event("CID-023", duplicateEventId, value.DocumentVersionId, now)], default));

        Assert.Null(await repository.GetVersionAsync(failed.DocumentVersionId, default));
        await using (var sql = new NpgsqlConnection(Runtime("evidence")))
        {
            Assert.Equal(0, await sql.ExecuteScalarAsync<int>("""
                SELECT count(*) FROM evidence.idempotency_operations
                WHERE customer_id=@CustomerId AND idempotency_key=@IdempotencyKey;
                """, new { failedIdentity.CustomerId, failedIdentity.IdempotencyKey }));
        }

        var retry = await repository.SaveVersionAsync(failedIdentity, failed,
            value => [Event("CID-023", $"event-retry-{suffix}", value.DocumentVersionId, now)], default);
        Assert.True(retry.Created);
        Assert.NotNull(await repository.GetVersionAsync(failed.DocumentVersionId, default));

        var conflict = failed with { ContentSha256 = new string('b', 64) };
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.SaveVersionAsync(failedIdentity, conflict,
            value => [Event("CID-023", $"event-conflict-{suffix}", value.DocumentVersionId, now)], default));
        Assert.Equal(failed.ContentSha256,
            (await repository.GetVersionAsync(failed.DocumentVersionId, default))!.ContentSha256);
    }

    [Fact]
    public async Task ConcurrentEvidenceVersionsAreSerializedWithoutLoss()
    {
        var configuration = Configuration("Evidence");
        var suffix = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        var first = EvidenceCandidate(suffix, "concurrent-a", now);
        var second = EvidenceCandidate(suffix, "concurrent-b", now);
        await using var repository = new PostgresEvidenceRepository(configuration);

        var saves = await Task.WhenAll(
            repository.SaveVersionAsync(
                new("CreateDocumentVersion", "1.0.0", first.CustomerId, $"key-concurrent-a-{suffix}"), first,
                value => [Event("CID-023", $"event-concurrent-a-{suffix}", value.DocumentVersionId, now)], default),
            repository.SaveVersionAsync(
                new("CreateDocumentVersion", "1.0.0", second.CustomerId, $"key-concurrent-b-{suffix}"), second,
                value => [Event("CID-024", $"event-concurrent-b-{suffix}", value.DocumentVersionId, now)], default));

        Assert.Equal([1, 2], saves.Select(item => item.Version.Version).Order().ToArray());
        Assert.Equal(2, (await repository.GetDocumentAsync(first.DocumentId, default)).Count);
    }

    [Fact]
    public async Task FinancialProfileHistoryProvenanceIdempotencyAndOutboxAreDurable()
    {
        var configuration = Configuration("FinancialProfile");
        var suffix = Guid.NewGuid().ToString("N");
        var customer = $"customer-{suffix}";
        var now = DateTimeOffset.UtcNow;
        var security = new TrustedSecurityContext(new($"actor-{suffix}", "HUMAN", now, $"authn-{suffix}"),
            new("financial-profile", $"workload-{suffix}"), new("D09_TEST", null, $"authorization-{suffix}", customer));
        var payload = new NormalizeSourceFacts($"profile-{suffix}", customer, $"processing-{suffix}",
            [new($"source-fact-{suffix}", customer, FinancialObjectTypes.Income, "Synthetic income", 100m,
                "INR", new DateOnly(2026, 9, 1), "fixture:1", 1m, $"evidence-{suffix}",
                $"document-version-{suffix}", "extract-1", "validate-1")], "normalize-1", now);
        var request = new ContractRequest<NormalizeSourceFacts>(Vs02ContractNames.NormalizeSourceFacts,
            ContractGuard.CurrentVersion, $"request-{suffix}", $"correlation-{suffix}", null, security,
            $"key-{suffix}", payload);
        var identity = FinancialNormalizationIdentity.From(request);
        var transition = new FinancialProfileChangeTransition(now, request.CorrelationId, request.RequestId);
        FinancialNormalizationSave first;
        await using (var repository = new PostgresFinancialProfileRepository(configuration))
        {
            first = await repository.NormalizeAsync(identity, request, transition, default);
            Assert.True(first.Persisted);
        }
        await using (var reconstructed = new PostgresFinancialProfileRepository(configuration))
        {
            var profile = await reconstructed.GetProfileAsync(payload.FinancialProfileId, default);
            Assert.NotNull(profile);
            Assert.Single(profile.Facts);
            Assert.NotNull(await reconstructed.GetProvenanceAsync(first.Provenance[0].FinancialProvenanceId, default));
            var replay = await reconstructed.NormalizeAsync(identity, request, transition, default);
            Assert.False(replay.Persisted);
            Assert.Equal(first.Facts[0].FinancialFactId, replay.Facts[0].FinancialFactId);

            var revisedPayload = payload with
            {
                Facts = [payload.Facts[0] with
                {
                    SourceFactId = $"source-fact-revised-{suffix}",
                    CandidateValue = 125m,
                    EvidenceId = $"evidence-revised-{suffix}",
                    DocumentVersionId = $"document-version-revised-{suffix}"
                }]
            };
            var revisedRequest = request with
            {
                RequestId = $"request-revised-{suffix}",
                CorrelationId = $"correlation-revised-{suffix}",
                IdempotencyKey = $"key-revised-{suffix}",
                Payload = revisedPayload
            };
            var revised = await reconstructed.NormalizeAsync(FinancialNormalizationIdentity.From(revisedRequest),
                revisedRequest, new(now.AddMinutes(1), revisedRequest.CorrelationId, revisedRequest.RequestId), default);
            Assert.Equal(2, revised.Profile.Revision);
            Assert.Equal(2, revised.Facts[0].Revision);
            Assert.Equal(125m, (await reconstructed.GetFactAsync(revised.Facts[0].FinancialFactId, default))!.Value);

            var otherCustomerRequest = revisedRequest with
            {
                RequestId = $"request-other-{suffix}",
                CorrelationId = $"correlation-other-{suffix}",
                IdempotencyKey = $"key-other-{suffix}",
                Security = revisedRequest.Security with
                {
                    Access = revisedRequest.Security.Access with { CustomerId = $"other-customer-{suffix}" }
                },
                Payload = revisedPayload with { CustomerId = $"other-customer-{suffix}" }
            };
            await Assert.ThrowsAsync<InvalidOperationException>(() => reconstructed.NormalizeAsync(
                FinancialNormalizationIdentity.From(otherCustomerRequest), otherCustomerRequest,
                new(now.AddMinutes(2), otherCustomerRequest.CorrelationId, otherCustomerRequest.RequestId), default));
        }
        await using var sql = new NpgsqlConnection(Runtime("financial-profile"));
        Assert.Equal(4, await sql.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM financial_profile.outbox WHERE correlation_id=@First OR correlation_id=@Second;",
            new { First = request.CorrelationId, Second = $"correlation-revised-{suffix}" }));
        Assert.Equal(2, await sql.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM financial_profile.fact_revisions WHERE financial_profile_id=@Id;",
            new { Id = payload.FinancialProfileId }));
    }

    [Fact]
    public async Task FinancialRulesResultLineageIdempotencyAndOutboxAreDurable()
    {
        var configuration = Configuration("FinancialRules");
        var suffix = Guid.NewGuid().ToString("N");
        var identity = new CalculationRequestIdentity("ExecuteCalculation", "1.0.0", $"customer-{suffix}", $"key-{suffix}");
        var now = DateTimeOffset.UtcNow;
        var provenance = new FinancialProvenance($"provenance-{suffix}", $"fact-{suffix}", identity.CustomerId,
            $"evidence-{suffix}", $"version-{suffix}", $"source-{suffix}", "extract-1", "validate-1",
            "normalize-1", $"actor-{suffix}", $"workload-{suffix}", now, $"correlation-{suffix}", null);
        var inputs = ImmutableArray.Create(new CalculationInputLineage("left", provenance.FinancialFactId, 1,
            10m, "INR", provenance));
        var error = new ContractError("fixture.failure", ContractErrorCategory.ProcessingFailed,
            "Synthetic failure.", false, provenance.CorrelationId);
        var outcome = new DomainEvent<CalculationOutcomePayload>("CID-041", $"event-{suffix}",
            "CalculationFailed", "1.0.0", now, provenance.CorrelationId, $"request-{suffix}",
            "Financial Rules Service", "financial-calculation", $"calculation-{suffix}",
            new(identity.CustomerId, $"calculation-{suffix}", $"lineage-{suffix}",
                "engineering.sum", "1.0.0", 1, error.Code));
        var execution = new CalculationExecution(outcome.SubjectId, identity.CustomerId, inputs, null, error, outcome);
        await using (var repository = new PostgresCalculationRepository(configuration))
        {
            Assert.Equal(execution.CalculationId, repository.Commit(identity, $"fingerprint-{suffix}", () => execution).CalculationId);
        }
        await using (var reconstructed = new PostgresCalculationRepository(configuration))
        {
            var replay = reconstructed.FindRequest(identity, $"fingerprint-{suffix}");
            Assert.NotNull(replay);
            Assert.Equal(execution.CalculationId, replay.CalculationId);
            Assert.Equal(provenance.FinancialProvenanceId, replay.Inputs[0].Provenance.FinancialProvenanceId);
            Assert.Single(reconstructed.PendingEvents(), item => item.EventId == outcome.EventId);
        }

        var concurrentIdentity = identity with { IdempotencyKey = $"concurrent-{suffix}" };
        var executions = 0;
        await using var left = new PostgresCalculationRepository(configuration);
        await using var right = new PostgresCalculationRepository(configuration);
        var concurrent = await Task.WhenAll(
            Task.Run(() => left.Commit(concurrentIdentity, $"concurrent-fingerprint-{suffix}", () =>
            {
                Interlocked.Increment(ref executions);
                return execution with
                {
                    CalculationId = $"concurrent-calculation-{suffix}",
                    Event = execution.Event with
                    {
                        EventId = $"concurrent-event-{suffix}",
                        SubjectId = $"concurrent-calculation-{suffix}"
                    }
                };
            })),
            Task.Run(() => right.Commit(concurrentIdentity, $"concurrent-fingerprint-{suffix}", () =>
            {
                Interlocked.Increment(ref executions);
                return execution with
                {
                    CalculationId = $"concurrent-calculation-{suffix}",
                    Event = execution.Event with
                    {
                        EventId = $"concurrent-event-{suffix}",
                        SubjectId = $"concurrent-calculation-{suffix}"
                    }
                };
            })));
        Assert.Equal(1, executions);
        Assert.Single(concurrent.Select(item => item.CalculationId).Distinct());
    }

    [Fact]
    public async Task AuditIsAppendOnlyDeduplicatedAndDurable()
    {
        var configuration = Configuration("Audit");
        var suffix = Guid.NewGuid().ToString("N");
        var source = new AuditableEvent("CID-023", $"source-{suffix}", "DocumentVersionCreated", "1.0.0",
            DateTimeOffset.UtcNow, $"correlation-{suffix}", null, "Evidence Service", "document-version", $"version-{suffix}");
        await using (var repository = new PostgresAuditEvidenceRepository(configuration))
        {
            Assert.True((await repository.AppendAsync(source, DateTimeOffset.UtcNow, default)).Created);
            Assert.False((await repository.AppendAsync(source, DateTimeOffset.UtcNow, default)).Created);
        }
        await using var reconstructed = new PostgresAuditEvidenceRepository(configuration);
        Assert.Contains(await reconstructed.ReadAllAsync(default), item => item.SourceEventId == source.EventId);
        await using var runtime = new NpgsqlConnection(Runtime("audit"));
        await runtime.OpenAsync();
        await Assert.ThrowsAnyAsync<PostgresException>(() => runtime.ExecuteAsync(
            "UPDATE audit.evidence SET event_name='forbidden' WHERE source_event_id=@Id;", new { Id = source.EventId }));
        await Assert.ThrowsAnyAsync<PostgresException>(() => runtime.ExecuteAsync(
            "DELETE FROM audit.evidence WHERE source_event_id=@Id;", new { Id = source.EventId }));
    }

    private static readonly string[] Services = ["evidence", "financial-profile", "financial-rules", "reporting", "audit"];
    private static IConfiguration Configuration(string service)
    {
        var serviceId = service switch
        {
            "FinancialProfile" => "financial-profile",
            "FinancialRules" => "financial-rules",
            _ => service.ToLowerInvariant(),
        };
        return new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Monergy:ExecutionZone"] = "CI_EPHEMERAL",
            [$"Monergy:Persistence:{service}:RuntimeConnection"] = Runtime(serviceId),
            ["Monergy:Persistence:Evidence:S3:Endpoint"] = Required("D09_S3_ENDPOINT"),
            ["Monergy:Persistence:Evidence:S3:Bucket"] = "monergy-evidence-d09",
            ["Monergy:Persistence:Evidence:S3:AccessKey"] = Required("D09_S3_ACCESS_KEY"),
            ["Monergy:Persistence:Evidence:S3:SecretKey"] = Required("D09_S3_SECRET_KEY"),
        }).Build();
    }
    private static string Runtime(string service) => Required($"D09_{service.Replace('-', '_').ToUpperInvariant()}_RUNTIME_CONNECTION");
    private static string Owner(string service) => Required($"D09_{service.Replace('-', '_').ToUpperInvariant()}_OWNER_CONNECTION");
    private static string WithDatabase(string connection, string database)
    {
        var builder = new NpgsqlConnectionStringBuilder(connection) { Database = database };
        return builder.ConnectionString;
    }
    private static EvidenceVersionRecord EvidenceCandidate(string suffix, string discriminator, DateTimeOffset now) =>
        new($"document-{suffix}", $"version-{discriminator}-{suffix}", $"evidence-{discriminator}-{suffix}",
            $"customer-{suffix}", 0, "synthetic-fixture", $"{discriminator}.txt", "text/plain",
            $"s3://monergy-evidence-d09/content-{discriminator}-{suffix}", new string('a', 64), now, now, 1,
            $"content-{discriminator}-{suffix}", $"storage-version-{discriminator}-{suffix}");
    private static string Required(string name) => Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Required D09 test environment '{name}' is unavailable.");
    private static object Event(string contractId, string eventId, string subjectId, DateTimeOffset occurredAt) => new
    {
        contractId,
        eventId,
        eventName = "DocumentVersionCreated",
        eventVersion = "1.0.0",
        occurredAt,
        correlationId = $"correlation-{eventId}",
        causationId = (string?)null,
        producer = "Evidence Service",
        subjectType = "document-version",
        subjectId,
        payload = new { documentVersionId = subjectId },
    };
    private sealed record RetainedContent(string Reference, string Content);
}
