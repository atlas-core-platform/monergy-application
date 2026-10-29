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

public sealed partial class PersistentFoundationTests
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

        foreach (var service in new[] { "evidence", "financial-profile", "financial-rules", "reporting" })
        {
            var schema = service.Replace('-', '_');
            var outbox = $"{schema}.outbox";
            await using var outboxRuntime = new NpgsqlConnection(Runtime(service));
            await outboxRuntime.OpenAsync();

            Assert.False(await outboxRuntime.ExecuteScalarAsync<bool>(
                "SELECT has_table_privilege(current_user, @Outbox, 'UPDATE');", new { Outbox = outbox }));
            Assert.True(await outboxRuntime.ExecuteScalarAsync<bool>(
                "SELECT has_column_privilege(current_user, @Outbox, 'dispatched_at', 'UPDATE');",
                new { Outbox = outbox }));
        }
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
            Assert.Equal(125m, (await reconstructed.GetFactAsync(revised.Facts[0].FinancialFactId, null, default))!.Value);

            // Historical source types remain immutable even after a later revision uses a new source.
            var retypedRequest = request with
            {
                RequestId = $"request-retyped-{suffix}", IdempotencyKey = $"key-retyped-{suffix}",
                Payload = payload with { Facts = [payload.Facts[0] with { FactType = FinancialObjectTypes.Property }] }
            };
            await Assert.ThrowsAsync<InvalidOperationException>(() => reconstructed.NormalizeAsync(
                FinancialNormalizationIdentity.From(retypedRequest), retypedRequest, transition, default));
            Assert.Equal(2, (await reconstructed.GetProfileAsync(payload.FinancialProfileId, default))!.Revision);

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

    [Fact]
    [Trait("Category", "Physical")]
    public async Task ReviewedLoanRecordIsAtomicReplaySafeScopedAndAppendOnlyAfterReconstruction()
    {
        var configuration=Configuration("FinancialProfile"); var suffix=Guid.NewGuid().ToString("N"); var customer="customer-loan-"+suffix;
        var now=DateTimeOffset.UtcNow;
        var security=new TrustedSecurityContext(new("actor-loan","CUSTOMER",now,"authn-loan"),new("financial-profile","workload-loan"),new("D09_TEST",null,"authorization-loan",customer));
        var request=new ContractRequest<NormalizeSourceFacts>(Vs02ContractNames.NormalizeSourceFacts,ContractGuard.CurrentVersion,
            "request-"+suffix,"correlation-"+suffix,null,security,"source-"+suffix,
            new("profile-"+suffix,customer,"processing-"+suffix,[new("source-"+suffix,customer,"LOAN","Loan source",100000m,"USD",new(2026,1,1),"line 1",1m,"evidence-"+suffix,"document-"+suffix,"extract/1","validate/1")],"normalize/1",now));
        await using var financial=new PostgresFinancialProfileRepository(configuration);
        var normalized=await financial.NormalizeAsync(FinancialNormalizationIdentity.From(request),request,new(now,request.CorrelationId,request.RequestId),default);
        var source=Assert.Single(normalized.Facts);
        var input=new CreateLoanRecord(suffix,new("Home loan","Example lender","HOME",500000m,400000m,8.25m,10000m,60,new(2026,1,1),new(2030,12,31),new(2026,10,1),source.FinancialFactId,1));
        ReviewedLoanRecord saved;
        ReviewedLoanRecord corrected;
        ReviewedLoanRecord closed;
        ReviewLoanRecordStatus statusReview;
        ReviseLoanRecord correction;
        await using(var records=new PostgresLoanRecords(configuration))
        {
            var app=new LoanRecordsApplication(financial,records,TimeProvider.System);
            saved=await app.CreateAsync("T001",customer,"actor-loan",input,default);
            Assert.Equal(saved,await app.CreateAsync("T001",customer,"actor-loan",input,default));
            Assert.Equal(409,(await Assert.ThrowsAsync<Monergy.Platform.TenantBoundaryException>(()=>app.CreateAsync("T001",customer,"actor-loan",input with {Loan=input.Loan with {Outstanding=390000}},default))).Status);
            correction = new(Guid.NewGuid().ToString("N"),1,input.Loan with {Outstanding=390000m});
            corrected = await app.ReviseAsync("T001",customer,"actor-loan",saved.LoanRecordId,correction,default);
            Assert.Equal(2,corrected.Revision);
            statusReview = new(Guid.NewGuid().ToString("N"),corrected.Revision,"CLOSED",new(2026,10,19));
            closed = await app.ReviewStatusAsync("T001",customer,"actor-loan",saved.LoanRecordId,statusReview,default);
            Assert.Equal(corrected.Loan,closed.Loan);Assert.Equal("CLOSED",closed.LifecycleStatus);
        }
        await using(var reconstructed=new PostgresLoanRecords(configuration))
        {
            Assert.Equal(closed,await reconstructed.ReadAsync("T001",customer,saved.LoanRecordId,default));
            Assert.Equal(saved,await reconstructed.ReadAsync("T001",customer,saved.LoanRecordId,default,1));
            Assert.Equal(corrected,await new LoanRecordsApplication(financial,reconstructed,TimeProvider.System).ReviseAsync("T001",customer,"actor-loan",saved.LoanRecordId,correction,default));
            var app = new LoanRecordsApplication(financial,reconstructed,TimeProvider.System);
            Assert.Equal(closed,await app.ReviewStatusAsync("T001",customer,"actor-loan",saved.LoanRecordId,statusReview,default));
            var reopened = await app.ReviewStatusAsync("T001",customer,"actor-loan",saved.LoanRecordId,new(Guid.NewGuid().ToString("N"),closed.Revision,"ACTIVE",new(2026,10,19)),default);
            Assert.Equal("ACTIVE",reopened.LifecycleStatus);Assert.Equal(closed.Loan,reopened.Loan);
            Assert.Equal(closed,await app.ReviewStatusAsync("T001",customer,"actor-loan",saved.LoanRecordId,statusReview,default));
            Assert.Equal(409,(await Assert.ThrowsAsync<Monergy.Platform.TenantBoundaryException>(()=>app.ReviseAsync("T001",customer,"actor-loan",saved.LoanRecordId,correction with {OperationId=Guid.NewGuid().ToString("N")},default))).Status);
            Assert.Equal([reopened,closed,corrected,saved],(await app.HistoryAsync("T001",customer,saved.LoanRecordId,null,default)).Items);
            Assert.Null(await reconstructed.ReadAsync("T002",customer,saved.LoanRecordId,default));
            Assert.Single((await reconstructed.ListAsync("T001",customer,default)).Items);
        }
        await using var sql=new NpgsqlConnection(Runtime("financial-profile"));
        var denied=await Assert.ThrowsAsync<PostgresException>(()=>sql.ExecuteAsync("UPDATE financial_profile.loan_records SET payload=payload WHERE loan_record_id=@Id;",new {Id=saved.LoanRecordId}));
        Assert.Equal("42501",denied.SqlState);
        var correctionDenied=await Assert.ThrowsAsync<PostgresException>(()=>sql.ExecuteAsync("UPDATE financial_profile.loan_record_revisions SET payload=payload WHERE loan_record_id=@Id;",new {Id=saved.LoanRecordId}));
        Assert.Equal("42501",correctionDenied.SqlState);
        Assert.Equal(1,await sql.ExecuteScalarAsync<int>("SELECT count(*) FROM financial_profile.loan_records WHERE tenant_id='T001' AND customer_id=@Customer;",new {Customer=customer}));
        Assert.Equal(3,await sql.ExecuteScalarAsync<int>("SELECT count(*) FROM financial_profile.loan_record_revisions WHERE tenant_id='T001' AND customer_id=@Customer;",new {Customer=customer}));
    }

    [Fact]
    public async Task ReviewedInvestmentRecordRetainsExactSourceAcrossRestartAndRuntimeCannotRewriteIt()
    {
        var configuration=Configuration("FinancialProfile"); var suffix=Guid.NewGuid().ToString("N"); var customer="customer-investment-"+suffix;
        var now=DateTimeOffset.UtcNow;
        var security=new TrustedSecurityContext(new("actor-investment","CUSTOMER",now,"authn-investment"),new("financial-profile","workload-investment"),new("D09_TEST",null,"authorization-investment",customer));
        var request=new ContractRequest<NormalizeSourceFacts>(Vs02ContractNames.NormalizeSourceFacts,ContractGuard.CurrentVersion,
            "request-"+suffix,"correlation-"+suffix,null,security,"source-"+suffix,
            new("profile-"+suffix,customer,"processing-"+suffix,[new("source-"+suffix,customer,"INVESTMENT","Purchase value",100000m,"USD",new(2026,1,1),"line 1",1m,"evidence-"+suffix,"document-"+suffix,"extract/1","validate/1")],"normalize/1",now));
        await using var financial=new PostgresFinancialProfileRepository(configuration);
        var normalized=await financial.NormalizeAsync(FinancialNormalizationIdentity.From(request),request,new(now,request.CorrelationId,request.RequestId),default);
        var source=Assert.Single(normalized.Facts);
        var input=new CreateInvestmentRecord(suffix,new("Retirement holding","Example institution","Mutual fund",500000m,400000m,new(2026,10,1),source.FinancialFactId,1));
        ReviewedInvestmentRecord saved;
        ReviewedInvestmentRecord corrected;
        ReviseInvestmentRecord correction;
        await using(var records=new PostgresInvestmentRecords(configuration))
        {
            var app=new InvestmentRecordsApplication(financial,records,TimeProvider.System);
            saved=await app.CreateAsync("T001",customer,"actor-investment",input,default);
            Assert.Equal(saved,await app.CreateAsync("T001",customer,"actor-investment",input,default));
            Assert.Equal(409,(await Assert.ThrowsAsync<Monergy.Platform.TenantBoundaryException>(()=>app.CreateAsync("T001",customer,"actor-investment",input with {Investment=input.Investment with {CurrentValue=390000}},default))).Status);
            correction = new(Guid.NewGuid().ToString("N"),1,input.Investment with {CurrentValue=390000m});
            corrected = await app.ReviseAsync("T001",customer,"actor-investment",saved.InvestmentRecordId,correction,default);
            Assert.Equal(2,corrected.Revision);
        }
        await using(var reconstructed=new PostgresInvestmentRecords(configuration))
        {
            Assert.Equal(corrected,await reconstructed.ReadAsync("T001",customer,saved.InvestmentRecordId,default));
            Assert.Equal(saved,await reconstructed.ReadAsync("T001",customer,saved.InvestmentRecordId,default,1));
            var owner = new InvestmentRecordsApplication(financial,reconstructed,TimeProvider.System);
            Assert.Equal(corrected,await owner.ReviseAsync("T001",customer,"actor-investment",saved.InvestmentRecordId,correction,default));
            Assert.Equal(409,(await Assert.ThrowsAsync<Monergy.Platform.TenantBoundaryException>(()=>owner.ReviseAsync("T001",customer,"actor-investment",saved.InvestmentRecordId,correction with {OperationId=Guid.NewGuid().ToString("N")},default))).Status);
            Assert.Equal([corrected,saved],(await owner.HistoryAsync("T001",customer,saved.InvestmentRecordId,null,default)).Items);
            Assert.Equal(saved,await new InvestmentRecordsApplication(financial,reconstructed,TimeProvider.System).CreateAsync("T001",customer,"actor-investment",input,default));
            Assert.Null(await reconstructed.ReadAsync("T002",customer,saved.InvestmentRecordId,default));
            Assert.Single((await reconstructed.ListAsync("T001",customer,default)).Items);
            Assert.Equal(corrected,Assert.Single((await reconstructed.SnapshotAsync("T001",customer,corrected.Investment.ValueAsOfDate,corrected.Currency,default)).Items));
            Assert.Empty((await reconstructed.SnapshotAsync("T002",customer,corrected.Investment.ValueAsOfDate,corrected.Currency,default)).Items);
        }
        await using var sql=new NpgsqlConnection(Runtime("financial-profile"));
        var denied=await Assert.ThrowsAsync<PostgresException>(()=>sql.ExecuteAsync("UPDATE financial_profile.investment_records SET payload=payload WHERE investment_record_id=@Id;",new {Id=saved.InvestmentRecordId}));
        Assert.Equal("42501",denied.SqlState);
        var correctionDenied=await Assert.ThrowsAsync<PostgresException>(()=>sql.ExecuteAsync("UPDATE financial_profile.investment_record_revisions SET payload=payload WHERE investment_record_id=@Id;",new {Id=saved.InvestmentRecordId}));
        Assert.Equal("42501",correctionDenied.SqlState);
        Assert.Equal(1,await sql.ExecuteScalarAsync<int>("SELECT count(*) FROM financial_profile.investment_records WHERE tenant_id='T001' AND customer_id=@Customer;",new {Customer=customer}));
    }

    [Fact]
    public async Task ReviewedBankAccountRecordPersistsExactSourceAndRuntimeHistoryIsAppendOnly()
    {
        var configuration=Configuration("FinancialProfile");var suffix=Guid.NewGuid().ToString("N");var customer="customer-bank-"+suffix;
        var now=DateTimeOffset.UtcNow;
        var security=new TrustedSecurityContext(new("actor-bank","CUSTOMER",now,"authn-bank"),new("financial-profile","workload-bank"),new("P2_01_TEST",null,"authorization-bank",customer));
        var request=new ContractRequest<NormalizeSourceFacts>(Vs02ContractNames.NormalizeSourceFacts,ContractGuard.CurrentVersion,
            "request-"+suffix,"correlation-"+suffix,null,security,"source-"+suffix,
            new("profile-"+suffix,customer,"processing-"+suffix,[new("source-"+suffix,customer,FinancialObjectTypes.BankAccount,
                "Reviewed balance",2510.25m,"INR",new(2026,10,1),"line 1",1m,"evidence-"+suffix,"document-"+suffix,
                "extract/1","validate/1")],"normalize/1",now));
        await using var financial=new PostgresFinancialProfileRepository(configuration);
        var normalized=await financial.NormalizeAsync(FinancialNormalizationIdentity.From(request),request,new(now,request.CorrelationId,request.RequestId),default);
        var source=Assert.Single(normalized.Facts);
        var input=new CreateBankAccountRecord(suffix,new("Example Bank","Savings","4821",source.FinancialFactId,1));
        ReviewedBankAccountRecord saved;ReviewedBankAccountRecord corrected;
        await using(var records=new PostgresBankAccountRecords(configuration))
        {
            var owner=new BankAccountRecordsApplication(financial,records,TimeProvider.System);
            saved=await owner.CreateAsync("T001",customer,"actor-bank",input,default);
            Assert.Equal(2510.25m,saved.Balance);Assert.Equal("INR",saved.Currency);Assert.Equal(new DateOnly(2026,10,1),saved.BalanceAsOfDate);
            Assert.Equal(saved,await owner.CreateAsync("T001",customer,"actor-bank",input,default));
            corrected=await owner.ReviseAsync("T001",customer,"actor-bank",saved.BankAccountRecordId,
                new(Guid.NewGuid().ToString("N"),1,saved.Account with{BankName="Updated Bank",AccountNumberLast4="7314"}),default);
        }
        await using(var records=new PostgresBankAccountRecords(configuration))
        {
            Assert.Equal(corrected,await records.ReadAsync("T001",customer,saved.BankAccountRecordId,default));
            Assert.Equal(saved,await records.ReadAsync("T001",customer,saved.BankAccountRecordId,default,1));
            Assert.Null(await records.ReadAsync("T002",customer,saved.BankAccountRecordId,default));
            Assert.Equal([corrected,saved],(await records.HistoryAsync("T001",customer,saved.BankAccountRecordId,null,default)).Items);
        }
        await using var sql=new NpgsqlConnection(Runtime("financial-profile"));
        foreach(var statement in new[]{
            "UPDATE financial_profile.bank_account_records SET payload=payload WHERE bank_account_record_id=@Id;",
            "DELETE FROM financial_profile.bank_account_record_revisions WHERE bank_account_record_id=@Id;",
            "TRUNCATE financial_profile.bank_account_record_revisions;"})
        {
            var denied=await Assert.ThrowsAsync<PostgresException>(()=>sql.ExecuteAsync(statement,new{Id=saved.BankAccountRecordId}));
            Assert.Equal("42501",denied.SqlState);
        }
        Assert.Equal(1,await sql.ExecuteScalarAsync<int>("SELECT count(*) FROM financial_profile.bank_account_records WHERE tenant_id='T001' AND customer_id=@Customer;",new{Customer=customer}));
        Assert.Equal(1,await sql.ExecuteScalarAsync<int>("SELECT count(*) FROM financial_profile.bank_account_record_revisions WHERE tenant_id='T001' AND customer_id=@Customer;",new{Customer=customer}));
    }

    [Fact]
    public async Task ReviewedPropertyRecordRetainsExactSourceAcrossRestartAndRuntimeCannotRewriteIt()
    {
        var configuration=Configuration("FinancialProfile"); var suffix=Guid.NewGuid().ToString("N"); var customer="customer-property-"+suffix;
        var now=DateTimeOffset.UtcNow;
        var security=new TrustedSecurityContext(new("actor-property","CUSTOMER",now,"authn-property"),new("financial-profile","workload-property"),new("D09_TEST",null,"authorization-property",customer));
        var request=new ContractRequest<NormalizeSourceFacts>(Vs02ContractNames.NormalizeSourceFacts,ContractGuard.CurrentVersion,
            "request-"+suffix,"correlation-"+suffix,null,security,"source-"+suffix,
            new("profile-"+suffix,customer,"processing-"+suffix,[new("source-"+suffix,customer,"PROPERTY","Purchase value",100000m,"USD",new(2026,1,1),"line 1",1m,"evidence-"+suffix,"document-"+suffix,"extract/1","validate/1")],"normalize/1",now));
        await using var financial=new PostgresFinancialProfileRepository(configuration);
        var normalized=await financial.NormalizeAsync(FinancialNormalizationIdentity.From(request),request,new(now,request.CorrelationId,request.RequestId),default);
        var source=Assert.Single(normalized.Facts);
        var input=new CreatePropertyRecord(suffix,new("Family home","Example location","Apartment",500000m,400000m,new(2026,10,1),source.FinancialFactId,1));
        ReviewedPropertyRecord saved;
        ReviewedPropertyRecord corrected;
        RevisePropertyRecord correction;
        await using(var records=new PostgresPropertyRecords(configuration))
        {
            var app=new PropertyRecordsApplication(financial,records,TimeProvider.System);
            saved=await app.CreateAsync("T001",customer,"actor-property",input,default);
            Assert.Equal(saved,await app.CreateAsync("T001",customer,"actor-property",input,default));
            Assert.Equal(409,(await Assert.ThrowsAsync<Monergy.Platform.TenantBoundaryException>(()=>app.CreateAsync("T001",customer,"actor-property",input with {Property=input.Property with {CurrentValue=390000}},default))).Status);
            correction = new(Guid.NewGuid().ToString("N"),1,input.Property with {CurrentValue=390000m});
            corrected = await app.ReviseAsync("T001",customer,"actor-property",saved.PropertyRecordId,correction,default);
            Assert.Equal(2,corrected.Revision);
        }
        await using(var reconstructed=new PostgresPropertyRecords(configuration))
        {
            Assert.Equal(corrected,await reconstructed.ReadAsync("T001",customer,saved.PropertyRecordId,default));
            Assert.Equal(saved,await reconstructed.ReadAsync("T001",customer,saved.PropertyRecordId,default,1));
            var owner = new PropertyRecordsApplication(financial,reconstructed,TimeProvider.System);
            Assert.Equal(corrected,await owner.ReviseAsync("T001",customer,"actor-property",saved.PropertyRecordId,correction,default));
            Assert.Equal(409,(await Assert.ThrowsAsync<Monergy.Platform.TenantBoundaryException>(()=>owner.ReviseAsync("T001",customer,"actor-property",saved.PropertyRecordId,correction with {OperationId=Guid.NewGuid().ToString("N")},default))).Status);
            Assert.Equal([corrected,saved],(await owner.HistoryAsync("T001",customer,saved.PropertyRecordId,null,default)).Items);
            Assert.Equal(saved,await new PropertyRecordsApplication(financial,reconstructed,TimeProvider.System).CreateAsync("T001",customer,"actor-property",input,default));
            Assert.Null(await reconstructed.ReadAsync("T002",customer,saved.PropertyRecordId,default));
            Assert.Single((await reconstructed.ListAsync("T001",customer,default)).Items);
            Assert.Equal(corrected,Assert.Single((await reconstructed.SnapshotAsync("T001",customer,corrected.Property.ValueAsOfDate,corrected.Currency,default)).Items));
            Assert.Empty((await reconstructed.SnapshotAsync("T002",customer,corrected.Property.ValueAsOfDate,corrected.Currency,default)).Items);
        }
        await using var sql=new NpgsqlConnection(Runtime("financial-profile"));
        var denied=await Assert.ThrowsAsync<PostgresException>(()=>sql.ExecuteAsync("UPDATE financial_profile.property_records SET payload=payload WHERE property_record_id=@Id;",new {Id=saved.PropertyRecordId}));
        Assert.Equal("42501",denied.SqlState);
        var correctionDenied=await Assert.ThrowsAsync<PostgresException>(()=>sql.ExecuteAsync("UPDATE financial_profile.property_record_revisions SET payload=payload WHERE property_record_id=@Id;",new {Id=saved.PropertyRecordId}));
        Assert.Equal("42501",correctionDenied.SqlState);
        Assert.Equal(1,await sql.ExecuteScalarAsync<int>("SELECT count(*) FROM financial_profile.property_records WHERE tenant_id='T001' AND customer_id=@Customer;",new {Customer=customer}));
    }

    [Fact]
    public async Task ReviewedVehicleRecordRetainsExactSourceAcrossRestartAndRuntimeCannotRewriteIt()
    {
        var configuration=Configuration("FinancialProfile"); var suffix=Guid.NewGuid().ToString("N"); var customer="customer-vehicle-"+suffix;
        var now=DateTimeOffset.UtcNow;
        var security=new TrustedSecurityContext(new("actor-vehicle","CUSTOMER",now,"authn-vehicle"),new("financial-profile","workload-vehicle"),new("D09_TEST",null,"authorization-vehicle",customer));
        var request=new ContractRequest<NormalizeSourceFacts>(Vs02ContractNames.NormalizeSourceFacts,ContractGuard.CurrentVersion,
            "request-"+suffix,"correlation-"+suffix,null,security,"source-"+suffix,
            new("profile-"+suffix,customer,"processing-"+suffix,[new("source-"+suffix,customer,"VEHICLE","Purchase value",100000m,"USD",new(2026,1,1),"line 1",1m,"evidence-"+suffix,"document-"+suffix,"extract/1","validate/1")],"normalize/1",now));
        await using var financial=new PostgresFinancialProfileRepository(configuration);
        var normalized=await financial.NormalizeAsync(FinancialNormalizationIdentity.From(request),request,new(now,request.CorrelationId,request.RequestId),default);
        var source=Assert.Single(normalized.Facts);
        var input=new CreateVehicleRecord(suffix,new("Family car","Example registrationNumber","Car",500000m,400000m,new(2026,10,1),source.FinancialFactId,1));
        ReviewedVehicleRecord saved;
        ReviewedVehicleRecord corrected;
        ReviseVehicleRecord correction;
        await using(var records=new PostgresVehicleRecords(configuration))
        {
            var app=new VehicleRecordsApplication(financial,records,TimeProvider.System);
            saved=await app.CreateAsync("T001",customer,"actor-vehicle",input,default);
            Assert.Equal(saved,await app.CreateAsync("T001",customer,"actor-vehicle",input,default));
            Assert.Equal(409,(await Assert.ThrowsAsync<Monergy.Platform.TenantBoundaryException>(()=>app.CreateAsync("T001",customer,"actor-vehicle",input with {Vehicle=input.Vehicle with {CurrentValue=390000}},default))).Status);
            correction = new(Guid.NewGuid().ToString("N"),1,input.Vehicle with {CurrentValue=390000m});
            corrected = await app.ReviseAsync("T001",customer,"actor-vehicle",saved.VehicleRecordId,correction,default);
            Assert.Equal(2,corrected.Revision);
        }
        await using(var reconstructed=new PostgresVehicleRecords(configuration))
        {
            Assert.Equal(corrected,await reconstructed.ReadAsync("T001",customer,saved.VehicleRecordId,default));
            Assert.Equal(saved,await reconstructed.ReadAsync("T001",customer,saved.VehicleRecordId,default,1));
            var owner = new VehicleRecordsApplication(financial,reconstructed,TimeProvider.System);
            Assert.Equal(corrected,await owner.ReviseAsync("T001",customer,"actor-vehicle",saved.VehicleRecordId,correction,default));
            Assert.Equal(409,(await Assert.ThrowsAsync<Monergy.Platform.TenantBoundaryException>(()=>owner.ReviseAsync("T001",customer,"actor-vehicle",saved.VehicleRecordId,correction with {OperationId=Guid.NewGuid().ToString("N")},default))).Status);
            Assert.Equal([corrected,saved],(await owner.HistoryAsync("T001",customer,saved.VehicleRecordId,null,default)).Items);
            Assert.Equal(saved,await new VehicleRecordsApplication(financial,reconstructed,TimeProvider.System).CreateAsync("T001",customer,"actor-vehicle",input,default));
            Assert.Null(await reconstructed.ReadAsync("T002",customer,saved.VehicleRecordId,default));
            Assert.Single((await reconstructed.ListAsync("T001",customer,default)).Items);
            Assert.Equal(corrected,Assert.Single((await reconstructed.SnapshotAsync("T001",customer,corrected.Vehicle.ValueAsOfDate,corrected.Currency,default)).Items));
            Assert.Empty((await reconstructed.SnapshotAsync("T002",customer,corrected.Vehicle.ValueAsOfDate,corrected.Currency,default)).Items);
        }
        await using var sql=new NpgsqlConnection(Runtime("financial-profile"));
        var denied=await Assert.ThrowsAsync<PostgresException>(()=>sql.ExecuteAsync("UPDATE financial_profile.vehicle_records SET payload=payload WHERE vehicle_record_id=@Id;",new {Id=saved.VehicleRecordId}));
        Assert.Equal("42501",denied.SqlState);
        var correctionDenied=await Assert.ThrowsAsync<PostgresException>(()=>sql.ExecuteAsync("UPDATE financial_profile.vehicle_record_revisions SET payload=payload WHERE vehicle_record_id=@Id;",new {Id=saved.VehicleRecordId}));
        Assert.Equal("42501",correctionDenied.SqlState);
        Assert.Equal(1,await sql.ExecuteScalarAsync<int>("SELECT count(*) FROM financial_profile.vehicle_records WHERE tenant_id='T001' AND customer_id=@Customer;",new {Customer=customer}));
    }

    [Fact]
    public async Task ReviewedIncomeRecordRetainsPeriodAndReviewStatusAcrossRestartAndRuntimeCannotRewriteIt()
    {
        var configuration=Configuration("FinancialProfile");var suffix=Guid.NewGuid().ToString("N");var customer="customer-income-"+suffix;var now=DateTimeOffset.UtcNow;
        var security=new TrustedSecurityContext(new("actor-income","CUSTOMER",now,"authn-income"),new("financial-profile","workload-income"),new("D09_TEST",null,"authorization-income",customer));
        var request=new ContractRequest<NormalizeSourceFacts>(Vs02ContractNames.NormalizeSourceFacts,ContractGuard.CurrentVersion,"request-"+suffix,"correlation-"+suffix,null,security,"source-"+suffix,
            new("profile-"+suffix,customer,"processing-"+suffix,[new("source-"+suffix,customer,"INCOME","Net pay",100000m,"USD",new(2026,1,1),"line 1",1m,"evidence-"+suffix,"document-"+suffix,"extract/1","validate/1")],"normalize/1",now));
        await using var financial=new PostgresFinancialProfileRepository(configuration);
        var normalized=await financial.NormalizeAsync(FinancialNormalizationIdentity.From(request),request,new(now,request.CorrelationId,request.RequestId),default);
        var source=Assert.Single(normalized.Facts);
        var input=new CreateIncomeRecord(suffix,new("September salary","Example employer","Employment",400000m,"MONTHLY","2026-27",new(2026,9,1),new(2026,9,30),source.FinancialFactId,1));
        ReviewedIncomeRecord saved;
        ReviewedIncomeRecord corrected;
        ReviseIncomeRecord correction;
        await using(var records=new PostgresIncomeRecords(configuration))
        {
            var owner=new IncomeRecordsApplication(financial,records,TimeProvider.System);saved=await owner.CreateAsync("T001",customer,"actor-income",input,default);
            Assert.Equal("CUSTOMER_REVIEWED",saved.VerificationStatus);Assert.Equal(input.Income,saved.Income);
            Assert.Equal(saved,await owner.CreateAsync("T001",customer,"actor-income",input,default));
            Assert.Equal(409,(await Assert.ThrowsAsync<Monergy.Platform.TenantBoundaryException>(()=>owner.CreateAsync("T001",customer,"actor-income",input with {Income=input.Income with {Amount=1}},default))).Status);
            correction=new(Guid.NewGuid().ToString("N"),1,input.Income with {Amount=390000m,FinancialYear="Year ended September 2026"});
            corrected=await owner.ReviseAsync("T001",customer,"actor-income",saved.IncomeRecordId,correction,default);
            Assert.Equal(2,corrected.Revision);Assert.Equal("CUSTOMER_REVIEWED",corrected.VerificationStatus);
        }
        await using(var reconstructed=new PostgresIncomeRecords(configuration))
        {
            Assert.Equal(corrected,await reconstructed.ReadAsync("T001",customer,saved.IncomeRecordId,default));
            Assert.Equal(saved,await reconstructed.ReadAsync("T001",customer,saved.IncomeRecordId,default,1));
            var owner=new IncomeRecordsApplication(financial,reconstructed,TimeProvider.System);
            Assert.Equal(corrected,await owner.ReviseAsync("T001",customer,"actor-income",saved.IncomeRecordId,correction,default));
            Assert.Equal([corrected,saved],(await owner.HistoryAsync("T001",customer,saved.IncomeRecordId,null,default)).Items);
            Assert.Equal(409,(await Assert.ThrowsAsync<Monergy.Platform.TenantBoundaryException>(()=>owner.ReviseAsync("T001",customer,"actor-income",saved.IncomeRecordId,correction with {OperationId=Guid.NewGuid().ToString("N")},default))).Status);
            Assert.Equal(saved,await new IncomeRecordsApplication(financial,reconstructed,TimeProvider.System).CreateAsync("T001",customer,"actor-income",input,default));
            Assert.Null(await reconstructed.ReadAsync("T002",customer,saved.IncomeRecordId,default));Assert.Single((await reconstructed.ListAsync("T001",customer,default)).Items);
        }
        await using var sql=new NpgsqlConnection(Runtime("financial-profile"));
        var denied=await Assert.ThrowsAsync<PostgresException>(()=>sql.ExecuteAsync("UPDATE financial_profile.income_records SET payload=payload WHERE income_record_id=@Id;",new {Id=saved.IncomeRecordId}));
        Assert.Equal("42501",denied.SqlState);
        var correctionDenied=await Assert.ThrowsAsync<PostgresException>(()=>sql.ExecuteAsync("UPDATE financial_profile.income_record_revisions SET payload=payload WHERE income_record_id=@Id;",new {Id=saved.IncomeRecordId}));
        Assert.Equal("42501",correctionDenied.SqlState);Assert.Equal(1,await sql.ExecuteScalarAsync<int>("SELECT count(*) FROM financial_profile.income_records WHERE tenant_id='T001' AND customer_id=@Customer;",new {Customer=customer}));
    }

    [Fact]
    public async Task ReviewedExpenseRecordRetainsPeriodAndReviewStatusAcrossRestartAndRuntimeCannotRewriteIt()
    {
        var configuration=Configuration("FinancialProfile");var suffix=Guid.NewGuid().ToString("N");var customer="customer-expense-"+suffix;var now=DateTimeOffset.UtcNow;
        var security=new TrustedSecurityContext(new("actor-expense","CUSTOMER",now,"authn-expense"),new("financial-profile","workload-expense"),new("D09_TEST",null,"authorization-expense",customer));
        var request=new ContractRequest<NormalizeSourceFacts>(Vs02ContractNames.NormalizeSourceFacts,ContractGuard.CurrentVersion,"request-"+suffix,"correlation-"+suffix,null,security,"source-"+suffix,
            new("profile-"+suffix,customer,"processing-"+suffix,[new("source-"+suffix,customer,"EXPENSE","Expense stated in source",100000m,"USD",new(2026,1,1),"line 1",1m,"evidence-"+suffix,"document-"+suffix,"extract/1","validate/1")],"normalize/1",now));
        await using var financial=new PostgresFinancialProfileRepository(configuration);
        var normalized=await financial.NormalizeAsync(FinancialNormalizationIdentity.From(request),request,new(now,request.CorrelationId,request.RequestId),default);
        var source=Assert.Single(normalized.Facts);
        var input=new CreateExpenseRecord(suffix,new("September groceries","Groceries",400000m,new(2026,9,1),new(2026,9,30),source.FinancialFactId,1));
        ReviewedExpenseRecord saved;
        ReviewedExpenseRecord corrected;
        ReviseExpenseRecord correction;
        await using(var records=new PostgresExpenseRecords(configuration))
        {
            var owner=new ExpenseRecordsApplication(financial,records,TimeProvider.System);saved=await owner.CreateAsync("T001",customer,"actor-expense",input,default);
            Assert.Equal("CUSTOMER_REVIEWED",saved.VerificationStatus);Assert.Equal(input.Expense,saved.Expense);
            Assert.Equal(saved,await owner.CreateAsync("T001",customer,"actor-expense",input,default));
            Assert.Equal(409,(await Assert.ThrowsAsync<Monergy.Platform.TenantBoundaryException>(()=>owner.CreateAsync("T001",customer,"actor-expense",input with {Expense=input.Expense with {Amount=1}},default))).Status);
            correction=new(Guid.NewGuid().ToString("N"),1,input.Expense with {Amount=390000m,Category="Household supplies"});
            corrected=await owner.ReviseAsync("T001",customer,"actor-expense",saved.ExpenseRecordId,correction,default);
            Assert.Equal(2,corrected.Revision);Assert.Equal("CUSTOMER_REVIEWED",corrected.VerificationStatus);
        }
        await using(var reconstructed=new PostgresExpenseRecords(configuration))
        {
            Assert.Equal(corrected,await reconstructed.ReadAsync("T001",customer,saved.ExpenseRecordId,default));
            Assert.Equal(saved,await reconstructed.ReadAsync("T001",customer,saved.ExpenseRecordId,default,1));
            var owner=new ExpenseRecordsApplication(financial,reconstructed,TimeProvider.System);
            Assert.Equal(corrected,await owner.ReviseAsync("T001",customer,"actor-expense",saved.ExpenseRecordId,correction,default));
            Assert.Equal([corrected,saved],(await owner.HistoryAsync("T001",customer,saved.ExpenseRecordId,null,default)).Items);
            Assert.Equal(409,(await Assert.ThrowsAsync<Monergy.Platform.TenantBoundaryException>(()=>owner.ReviseAsync("T001",customer,"actor-expense",saved.ExpenseRecordId,correction with {OperationId=Guid.NewGuid().ToString("N")},default))).Status);
            Assert.Equal(saved,await new ExpenseRecordsApplication(financial,reconstructed,TimeProvider.System).CreateAsync("T001",customer,"actor-expense",input,default));
            Assert.Null(await reconstructed.ReadAsync("T002",customer,saved.ExpenseRecordId,default));Assert.Single((await reconstructed.ListAsync("T001",customer,default)).Items);
        }
        await using var sql=new NpgsqlConnection(Runtime("financial-profile"));
        var denied=await Assert.ThrowsAsync<PostgresException>(()=>sql.ExecuteAsync("UPDATE financial_profile.expense_records SET payload=payload WHERE expense_record_id=@Id;",new {Id=saved.ExpenseRecordId}));
        Assert.Equal("42501",denied.SqlState);
        var correctionDenied=await Assert.ThrowsAsync<PostgresException>(()=>sql.ExecuteAsync("UPDATE financial_profile.expense_record_revisions SET payload=payload WHERE expense_record_id=@Id;",new {Id=saved.ExpenseRecordId}));
        Assert.Equal("42501",correctionDenied.SqlState);Assert.Equal(1,await sql.ExecuteScalarAsync<int>("SELECT count(*) FROM financial_profile.expense_records WHERE tenant_id='T001' AND customer_id=@Customer;",new {Customer=customer}));
    }

    [Fact]
    public async Task ReviewedLiabilityRecordRetainsPeriodAndReviewStatusAcrossRestartAndRuntimeCannotRewriteIt()
    {
        var configuration=Configuration("FinancialProfile");var suffix=Guid.NewGuid().ToString("N");var customer="customer-liability-"+suffix;var now=DateTimeOffset.UtcNow;
        var security=new TrustedSecurityContext(new("actor-liability","CUSTOMER",now,"authn-liability"),new("financial-profile","workload-liability"),new("D09_TEST",null,"authorization-liability",customer));
        var request=new ContractRequest<NormalizeSourceFacts>(Vs02ContractNames.NormalizeSourceFacts,ContractGuard.CurrentVersion,"request-"+suffix,"correlation-"+suffix,null,security,"source-"+suffix,
            new("profile-"+suffix,customer,"processing-"+suffix,[new("source-"+suffix,customer,"LOAN","Liability stated in source",100000m,"USD",new(2026,1,1),"line 1",1m,"evidence-"+suffix,"document-"+suffix,"extract/1","validate/1")],"normalize/1",now));
        await using var financial=new PostgresFinancialProfileRepository(configuration);
        var normalized=await financial.NormalizeAsync(FinancialNormalizationIdentity.From(request),request,new(now,request.CorrelationId,request.RequestId),default);
        var source=Assert.Single(normalized.Facts);
        var input=new CreateLiabilityRecord(suffix,new("September debt","Example lender",400000m,new(2026,9,30),source.FinancialFactId,1));
        ReviewedLiabilityRecord saved;
        ReviewedLiabilityRecord corrected;
        ReviseLiabilityRecord correction;
        await using(var records=new PostgresLiabilityRecords(configuration))
        {
            var owner=new LiabilityRecordsApplication(financial,records,TimeProvider.System);saved=await owner.CreateAsync("T001",customer,"actor-liability",input,default);
            Assert.Equal("CUSTOMER_REVIEWED",saved.VerificationStatus);Assert.Equal(input.Liability,saved.Liability);
            Assert.Equal(saved,await owner.CreateAsync("T001",customer,"actor-liability",input,default));
            Assert.Equal(409,(await Assert.ThrowsAsync<Monergy.Platform.TenantBoundaryException>(()=>owner.CreateAsync("T001",customer,"actor-liability",input with {Liability=input.Liability with {OutstandingAmount=1}},default))).Status);
            correction=new(Guid.NewGuid().ToString("N"),1,input.Liability with {OutstandingAmount=390000m,Creditor="Household supplies"});
            corrected=await owner.ReviseAsync("T001",customer,"actor-liability",saved.LiabilityRecordId,correction,default);
            Assert.Equal(2,corrected.Revision);Assert.Equal("CUSTOMER_REVIEWED",corrected.VerificationStatus);
        }
        await using(var reconstructed=new PostgresLiabilityRecords(configuration))
        {
            Assert.Equal(corrected,await reconstructed.ReadAsync("T001",customer,saved.LiabilityRecordId,default));
            Assert.Equal(saved,await reconstructed.ReadAsync("T001",customer,saved.LiabilityRecordId,default,1));
            var owner=new LiabilityRecordsApplication(financial,reconstructed,TimeProvider.System);
            Assert.Equal(corrected,await owner.ReviseAsync("T001",customer,"actor-liability",saved.LiabilityRecordId,correction,default));
            Assert.Equal([corrected,saved],(await owner.HistoryAsync("T001",customer,saved.LiabilityRecordId,null,default)).Items);
            Assert.Equal(corrected,Assert.Single((await owner.SnapshotAsync("T001",customer,corrected.Liability.BalanceAsOfDate,corrected.Currency,default)).Items));
            Assert.Empty((await owner.SnapshotAsync("T002",customer,corrected.Liability.BalanceAsOfDate,corrected.Currency,default)).Items);
            Assert.Equal(409,(await Assert.ThrowsAsync<Monergy.Platform.TenantBoundaryException>(()=>owner.ReviseAsync("T001",customer,"actor-liability",saved.LiabilityRecordId,correction with {OperationId=Guid.NewGuid().ToString("N")},default))).Status);
            Assert.Equal(saved,await new LiabilityRecordsApplication(financial,reconstructed,TimeProvider.System).CreateAsync("T001",customer,"actor-liability",input,default));
            Assert.Null(await reconstructed.ReadAsync("T002",customer,saved.LiabilityRecordId,default));Assert.Single((await reconstructed.ListAsync("T001",customer,default)).Items);
        }
        await using var sql=new NpgsqlConnection(Runtime("financial-profile"));
        var denied=await Assert.ThrowsAsync<PostgresException>(()=>sql.ExecuteAsync("UPDATE financial_profile.liability_records SET payload=payload WHERE liability_record_id=@Id;",new {Id=saved.LiabilityRecordId}));
        Assert.Equal("42501",denied.SqlState);
        var correctionDenied=await Assert.ThrowsAsync<PostgresException>(()=>sql.ExecuteAsync("UPDATE financial_profile.liability_record_revisions SET payload=payload WHERE liability_record_id=@Id;",new {Id=saved.LiabilityRecordId}));
        Assert.Equal("42501",correctionDenied.SqlState);Assert.Equal(1,await sql.ExecuteScalarAsync<int>("SELECT count(*) FROM financial_profile.liability_records WHERE tenant_id='T001' AND customer_id=@Customer;",new {Customer=customer}));
    }

    [Fact]
    public async Task ReviewedInsuranceRecordRetainsExactSourceAcrossRestartAndRuntimeCannotRewriteIt()
    {
        var configuration=Configuration("FinancialProfile"); var suffix=Guid.NewGuid().ToString("N"); var customer="customer-insurance-"+suffix;
        var now=DateTimeOffset.UtcNow;
        var security=new TrustedSecurityContext(new("actor-insurance","CUSTOMER",now,"authn-insurance"),new("financial-profile","workload-insurance"),new("D09_TEST",null,"authorization-insurance",customer));
        var request=new ContractRequest<NormalizeSourceFacts>(Vs02ContractNames.NormalizeSourceFacts,ContractGuard.CurrentVersion,
            "request-"+suffix,"correlation-"+suffix,null,security,"source-"+suffix,
            new("profile-"+suffix,customer,"processing-"+suffix,[new("source-"+suffix,customer,"INSURANCE_POLICY","Purchase value",100000m,"USD",new(2026,1,1),"line 1",1m,"evidence-"+suffix,"document-"+suffix,"extract/1","validate/1")],"normalize/1",now));
        await using var financial=new PostgresFinancialProfileRepository(configuration);
        var normalized=await financial.NormalizeAsync(FinancialNormalizationIdentity.From(request),request,new(now,request.CorrelationId,request.RequestId),default);
        var source=Assert.Single(normalized.Facts);
        var input=new CreateInsuranceRecord(suffix,new("Family policy","Example provider","Life",500000m,400000m,new(2026,10,1),null,source.FinancialFactId,1));
        ReviewedInsuranceRecord saved;
        ReviewedInsuranceRecord corrected;
        ReviseInsuranceRecord correction;
        await using(var records=new PostgresInsuranceRecords(configuration))
        {
            var app=new InsuranceRecordsApplication(financial,records,TimeProvider.System);
            saved=await app.CreateAsync("T001",customer,"actor-insurance",input,default);
            Assert.Equal(saved,await app.CreateAsync("T001",customer,"actor-insurance",input,default));
            Assert.Equal(409,(await Assert.ThrowsAsync<Monergy.Platform.TenantBoundaryException>(()=>app.CreateAsync("T001",customer,"actor-insurance",input with {Insurance=input.Insurance with {PremiumAmount=390000}},default))).Status);
            correction = new(Guid.NewGuid().ToString("N"),1,input.Insurance with {PremiumAmount=390000m});
            corrected = await app.ReviseAsync("T001",customer,"actor-insurance",saved.InsuranceRecordId,correction,default);
            Assert.Equal(2,corrected.Revision);
        }
        await using(var reconstructed=new PostgresInsuranceRecords(configuration))
        {
            Assert.Equal(corrected,await reconstructed.ReadAsync("T001",customer,saved.InsuranceRecordId,default));
            Assert.Equal(saved,await reconstructed.ReadAsync("T001",customer,saved.InsuranceRecordId,default,1));
            var owner = new InsuranceRecordsApplication(financial,reconstructed,TimeProvider.System);
            Assert.Equal(corrected,await owner.ReviseAsync("T001",customer,"actor-insurance",saved.InsuranceRecordId,correction,default));
            Assert.Equal(409,(await Assert.ThrowsAsync<Monergy.Platform.TenantBoundaryException>(()=>owner.ReviseAsync("T001",customer,"actor-insurance",saved.InsuranceRecordId,correction with {OperationId=Guid.NewGuid().ToString("N")},default))).Status);
            Assert.Equal([corrected,saved],(await owner.HistoryAsync("T001",customer,saved.InsuranceRecordId,null,default)).Items);
            Assert.Equal(saved,await new InsuranceRecordsApplication(financial,reconstructed,TimeProvider.System).CreateAsync("T001",customer,"actor-insurance",input,default));
            Assert.Null(await reconstructed.ReadAsync("T002",customer,saved.InsuranceRecordId,default));
            Assert.Single((await reconstructed.ListAsync("T001",customer,default)).Items);
        }
        await using var sql=new NpgsqlConnection(Runtime("financial-profile"));
        var denied=await Assert.ThrowsAsync<PostgresException>(()=>sql.ExecuteAsync("UPDATE financial_profile.insurance_records SET payload=payload WHERE insurance_record_id=@Id;",new {Id=saved.InsuranceRecordId}));
        Assert.Equal("42501",denied.SqlState);
        var correctionDenied=await Assert.ThrowsAsync<PostgresException>(()=>sql.ExecuteAsync("UPDATE financial_profile.insurance_record_revisions SET payload=payload WHERE insurance_record_id=@Id;",new {Id=saved.InsuranceRecordId}));
        Assert.Equal("42501",correctionDenied.SqlState);
        Assert.Equal(1,await sql.ExecuteScalarAsync<int>("SELECT count(*) FROM financial_profile.insurance_records WHERE tenant_id='T001' AND customer_id=@Customer;",new {Customer=customer}));
    }


    [Fact]
    public async Task ReviewedTaxRecordRetainsPeriodAndReviewStatusAcrossRestartAndRuntimeCannotRewriteIt()
    {
        var configuration=Configuration("FinancialProfile");var suffix=Guid.NewGuid().ToString("N");var customer="customer-tax-"+suffix;var now=DateTimeOffset.UtcNow;
        var security=new TrustedSecurityContext(new("actor-tax","CUSTOMER",now,"authn-tax"),new("financial-profile","workload-tax"),new("D09_TEST",null,"authorization-tax",customer));
        var request=new ContractRequest<NormalizeSourceFacts>(Vs02ContractNames.NormalizeSourceFacts,ContractGuard.CurrentVersion,"request-"+suffix,"correlation-"+suffix,null,security,"source-"+suffix,
            new("profile-"+suffix,customer,"processing-"+suffix,[new("source-"+suffix,customer,"TAX_RECORD","Net pay",100000m,"USD",new(2026,1,1),"line 1",1m,"evidence-"+suffix,"document-"+suffix,"extract/1","validate/1")],"normalize/1",now));
        await using var financial=new PostgresFinancialProfileRepository(configuration);
        var normalized=await financial.NormalizeAsync(FinancialNormalizationIdentity.From(request),request,new(now,request.CorrelationId,request.RequestId),default);
        var source=Assert.Single(normalized.Facts);
        var input=new CreateTaxRecord(suffix,new("Tax statement","India","Income tax",400000m,"PAID","2026-27",new(2026,9,1),new(2026,9,30),source.FinancialFactId,1));
        ReviewedTaxRecord saved;
        ReviewedTaxRecord corrected;
        ReviseTaxRecord correction;
        await using(var records=new PostgresTaxRecords(configuration))
        {
            var owner=new TaxRecordsApplication(financial,records,TimeProvider.System);saved=await owner.CreateAsync("T001",customer,"actor-tax",input,default);
            Assert.Equal("CUSTOMER_REVIEWED",saved.VerificationStatus);Assert.Equal(input.Tax,saved.Tax);
            Assert.Equal(saved,await owner.CreateAsync("T001",customer,"actor-tax",input,default));
            Assert.Equal(409,(await Assert.ThrowsAsync<Monergy.Platform.TenantBoundaryException>(()=>owner.CreateAsync("T001",customer,"actor-tax",input with {Tax=input.Tax with {Amount=1}},default))).Status);
            correction=new(Guid.NewGuid().ToString("N"),1,input.Tax with {Amount=390000m,TaxYearLabel="Year ended September 2026"});
            corrected=await owner.ReviseAsync("T001",customer,"actor-tax",saved.TaxRecordId,correction,default);
            Assert.Equal(2,corrected.Revision);Assert.Equal("CUSTOMER_REVIEWED",corrected.VerificationStatus);
        }
        await using(var reconstructed=new PostgresTaxRecords(configuration))
        {
            Assert.Equal(corrected,await reconstructed.ReadAsync("T001",customer,saved.TaxRecordId,default));
            Assert.Equal(saved,await reconstructed.ReadAsync("T001",customer,saved.TaxRecordId,default,1));
            var owner=new TaxRecordsApplication(financial,reconstructed,TimeProvider.System);
            Assert.Equal(corrected,await owner.ReviseAsync("T001",customer,"actor-tax",saved.TaxRecordId,correction,default));
            Assert.Equal([corrected,saved],(await owner.HistoryAsync("T001",customer,saved.TaxRecordId,null,default)).Items);
            Assert.Equal(409,(await Assert.ThrowsAsync<Monergy.Platform.TenantBoundaryException>(()=>owner.ReviseAsync("T001",customer,"actor-tax",saved.TaxRecordId,correction with {OperationId=Guid.NewGuid().ToString("N")},default))).Status);
            Assert.Equal(saved,await new TaxRecordsApplication(financial,reconstructed,TimeProvider.System).CreateAsync("T001",customer,"actor-tax",input,default));
            Assert.Null(await reconstructed.ReadAsync("T002",customer,saved.TaxRecordId,default));Assert.Single((await reconstructed.ListAsync("T001",customer,default)).Items);
        }
        await using var sql=new NpgsqlConnection(Runtime("financial-profile"));
        var denied=await Assert.ThrowsAsync<PostgresException>(()=>sql.ExecuteAsync("UPDATE financial_profile.tax_records SET payload=payload WHERE tax_record_id=@Id;",new {Id=saved.TaxRecordId}));
        Assert.Equal("42501",denied.SqlState);
        var correctionDenied=await Assert.ThrowsAsync<PostgresException>(()=>sql.ExecuteAsync("UPDATE financial_profile.tax_record_revisions SET payload=payload WHERE tax_record_id=@Id;",new {Id=saved.TaxRecordId}));
        Assert.Equal("42501",correctionDenied.SqlState);Assert.Equal(1,await sql.ExecuteScalarAsync<int>("SELECT count(*) FROM financial_profile.tax_records WHERE tenant_id='T001' AND customer_id=@Customer;",new {Customer=customer}));
    }


    [Fact]
    public async Task CustomerDefinedGoalsRetainTargetsVersionsAndReceiptsAcrossRestartAndRuntimeCannotRewriteThem()
    {
        var configuration=Configuration("FinancialProfile");var suffix=Guid.NewGuid().ToString("N");var customer="customer-goal-"+suffix;
        var input=new CreateFinancialGoal(suffix,new("Education fund",100000m,"INR",new(2030,6,30)));
        CustomerFinancialGoal first;CustomerFinancialGoal second;ReviseFinancialGoal change;
        await using(var records=new PostgresFinancialGoals(configuration))
        {
            var owner=new FinancialGoalsApplication(records,TimeProvider.System);first=await owner.CreateAsync("T001",customer,"actor-goal",input,default);
            Assert.Equal("CUSTOMER_DEFINED",first.DeclarationStatus);Assert.Equal(input.Goal,first.Goal);
            change=new(Guid.NewGuid().ToString("N"),1,input.Goal with {TargetAmount=200000m,TargetDate=new(2031,1,1)});
            second=await owner.ReviseAsync("T001",customer,"actor-goal",first.FinancialGoalId,change,default);
        }
        await using(var records=new PostgresFinancialGoals(configuration))
        {
            var owner=new FinancialGoalsApplication(records,TimeProvider.System);
            Assert.Equal(first,await owner.CreateAsync("T001",customer,"actor-goal",input,default));
            Assert.Equal(second,await owner.ReviseAsync("T001",customer,"actor-goal",first.FinancialGoalId,change,default));
            Assert.Equal(second,await owner.ReadAsync("T001",customer,first.FinancialGoalId,default));
            Assert.Equal(first,await owner.ReadAsync("T001",customer,first.FinancialGoalId,default,1));
            Assert.Equal([second,first],(await owner.HistoryAsync("T001",customer,first.FinancialGoalId,null,default)).Items);
            Assert.Null(await records.ReadAsync("T002",customer,first.FinancialGoalId,default));
            Assert.Equal(409,(await Assert.ThrowsAsync<Monergy.Platform.TenantBoundaryException>(()=>owner.ReviseAsync("T001",customer,"actor-goal",first.FinancialGoalId,change with {OperationId=Guid.NewGuid().ToString("N")},default))).Status);
        }
        await using var sql=new NpgsqlConnection(Runtime("financial-profile"));
        foreach(var table in new[]{"financial_goals","financial_goal_revisions"})
        {
            var denied=await Assert.ThrowsAsync<PostgresException>(()=>sql.ExecuteAsync("UPDATE financial_profile."+table+" SET payload=payload WHERE financial_goal_id=@Id;",new {Id=first.FinancialGoalId}));Assert.Equal("42501",denied.SqlState);
            var deleted=await Assert.ThrowsAsync<PostgresException>(()=>sql.ExecuteAsync("DELETE FROM financial_profile."+table+" WHERE financial_goal_id=@Id;",new {Id=first.FinancialGoalId}));Assert.Equal("42501",deleted.SqlState);
        }
    }

    [Fact]
    public async Task GoalContributionsKeepExactHistoricalTargetsAfterRestartAndRuntimeCannotRewriteReports()
    {
        var configuration=Configuration("FinancialProfile");var customer="contribution-"+Guid.NewGuid().ToString("N");
        await using var goals=new PostgresFinancialGoals(configuration);var goalOwner=new FinancialGoalsApplication(goals,TimeProvider.System);
        var goal=await goalOwner.CreateAsync("T001",customer,"actor-goal",new(Guid.NewGuid().ToString("N"),new("Education fund",100000m,"INR",new(2030,1,1))),default);
        var input=new CreateGoalContribution(Guid.NewGuid().ToString("N"),new(goal.FinancialGoalId,1,1000m,new(2026,10,1)));
        ReportedGoalContribution first;ReportedGoalContribution second;ReviseGoalContribution change;
        await using(var records=new PostgresGoalContributions(configuration))
        {
            var owner=new GoalContributionsApplication(goals,records,TimeProvider.System);first=await owner.CreateAsync("T001",customer,"actor-goal",input,default);
            await goalOwner.ReviseAsync("T001",customer,"actor-goal",goal.FinancialGoalId,new(Guid.NewGuid().ToString("N"),1,goal.Goal with {Currency="USD",TargetAmount=200000}),default);
            change=new(Guid.NewGuid().ToString("N"),1,input.Contribution with {Amount=1250,ContributionDate=new(2026,10,2)});second=await owner.ReviseAsync("T001",customer,"actor-goal",first.GoalContributionId,change,default);
            Assert.Equal(goal.Goal,second.SourceGoal);Assert.Equal("CUSTOMER_REPORTED",second.DeclarationStatus);
            Assert.Equal(404,(await Assert.ThrowsAsync<Monergy.Platform.TenantBoundaryException>(()=>owner.CreateAsync("T002",customer,"actor-goal",input,default))).Status);
        }
        await using(var records=new PostgresGoalContributions(configuration))
        {
            var owner=new GoalContributionsApplication(goals,records,TimeProvider.System);Assert.Equal(first,await owner.CreateAsync("T001",customer,"actor-goal",input,default));Assert.Equal(second,await owner.ReviseAsync("T001",customer,"actor-goal",first.GoalContributionId,change,default));Assert.Equal([second,first],(await owner.HistoryAsync("T001",customer,first.GoalContributionId,null,default)).Items);Assert.Equal(second,Assert.Single((await owner.ListAsync("T001",customer,goal.FinancialGoalId,default)).Items));Assert.Equal(first,await owner.ReadAsync("T001",customer,first.GoalContributionId,default,1));Assert.Null(await records.ReadAsync("T002",customer,first.GoalContributionId,default));
            Assert.Equal(409,(await Assert.ThrowsAsync<Monergy.Platform.TenantBoundaryException>(()=>owner.ReviseAsync("T001",customer,"actor-goal",first.GoalContributionId,change with {OperationId=Guid.NewGuid().ToString("N")},default))).Status);
        }
        var withdrawal=new WithdrawGoalContribution(Guid.NewGuid().ToString("N"),2,"Duplicate customer report");
        ReportedGoalContribution withdrawn;
        await using(var records=new PostgresGoalContributions(configuration))
            withdrawn=await new GoalContributionsApplication(goals,records,TimeProvider.System).WithdrawAsync("T001",customer,"actor-goal",first.GoalContributionId,withdrawal,default);
        await using(var records=new PostgresGoalContributions(configuration))
        {
            var owner=new GoalContributionsApplication(goals,records,TimeProvider.System);
            Assert.Equal(withdrawn,await owner.WithdrawAsync("T001",customer,"actor-goal",first.GoalContributionId,withdrawal,default));
            Assert.Equal([withdrawn,second,first],(await owner.HistoryAsync("T001",customer,first.GoalContributionId,null,default)).Items);
            Assert.Equal(second.Contribution,withdrawn.Contribution);Assert.Equal("CUSTOMER_WITHDRAWN",withdrawn.DeclarationStatus);
            Assert.Equal(409,(await Assert.ThrowsAsync<Monergy.Platform.TenantBoundaryException>(()=>owner.ReviseAsync("T001",customer,"actor-goal",first.GoalContributionId,new(Guid.NewGuid().ToString("N"),3,first.Contribution),default))).Status);
        }
        await using var sql=new NpgsqlConnection(Runtime("financial-profile"));
        foreach(var table in new[]{"goal_contributions","goal_contribution_revisions"})
        {
            var denied=await Assert.ThrowsAsync<PostgresException>(()=>sql.ExecuteAsync("UPDATE financial_profile."+table+" SET payload=payload WHERE goal_contribution_id=@Id;",new {Id=first.GoalContributionId}));Assert.Equal("42501",denied.SqlState);
            var deleted=await Assert.ThrowsAsync<PostgresException>(()=>sql.ExecuteAsync("DELETE FROM financial_profile."+table+" WHERE goal_contribution_id=@Id;",new {Id=first.GoalContributionId}));Assert.Equal("42501",deleted.SqlState);
        }
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
