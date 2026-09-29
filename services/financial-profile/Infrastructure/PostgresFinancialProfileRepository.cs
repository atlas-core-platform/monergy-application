using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Configuration;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.FinancialProfile.Application;
using Npgsql;

namespace Monergy.Services.FinancialProfile.Infrastructure;

public sealed class PostgresFinancialProfileRepository : IFinancialProfileRepository, IAsyncDisposable
{
    private const string FactColumns = """
        financial_fact_id AS FinancialFactId, financial_profile_id AS FinancialProfileId,
        customer_id AS CustomerId, fact_type AS FactType, label AS Label, value AS Value,
        currency AS Currency, effective_date AS EffectiveDate, revision AS Revision,
        financial_provenance_id AS FinancialProvenanceId, source_fact_id AS SourceFactId
        """;
    private readonly NpgsqlDataSource dataSource;

    public PostgresFinancialProfileRepository(IConfiguration configuration)
    {
        PhysicalPersistenceGuard.EnsureAllowed(configuration);
        dataSource = NpgsqlDataSource.Create(PhysicalPersistenceGuard.Require(configuration,
            "Monergy:Persistence:FinancialProfile:RuntimeConnection"));
    }

    public string AdapterKind => "POSTGRESQL_DURABLE";

    public async Task<FinancialNormalizationSave> NormalizeAsync(
        FinancialNormalizationIdentity idempotencyIdentity, ContractRequest<NormalizeSourceFacts> request,
        FinancialProfileChangeTransition profileChange, CancellationToken cancellationToken)
    {
        var payloadJson = JsonSerializer.Serialize(request.Payload, ContractJson.Options);
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payloadJson)));
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "SELECT pg_advisory_xact_lock(hashtextextended(@Id, 0));",
            new { Id = request.Payload.FinancialProfileId }, transaction, cancellationToken: cancellationToken));

        var prior = await connection.QuerySingleOrDefaultAsync<IdempotencyRow>(new CommandDefinition("""
            SELECT payload_fingerprint AS PayloadFingerprint, result_payload::text AS ResultPayload
            FROM financial_profile.idempotency_operations
            WHERE contract_name=@ContractName AND contract_version=@ContractVersion
              AND customer_id=@CustomerId AND idempotency_key=@IdempotencyKey;
            """, idempotencyIdentity, transaction, cancellationToken: cancellationToken));
        if (prior is not null)
        {
            if (!string.Equals(prior.PayloadFingerprint, fingerprint, StringComparison.Ordinal))
                throw new InvalidOperationException("Idempotency key conflict.");
            var replay = JsonSerializer.Deserialize<NormalizationSnapshot>(prior.ResultPayload, ContractJson.Options)
                ?? throw new InvalidOperationException("Committed normalization result is unavailable.");
            await transaction.CommitAsync(cancellationToken);
            return new(replay.Facts, replay.Provenance, replay.Events.Cast<object>().ToArray(),
                replay.Profile, replay.CreatedFactIds.ToHashSet(StringComparer.Ordinal), false);
        }

        var currentProfile = await connection.QuerySingleOrDefaultAsync<ProfileRow>(new CommandDefinition("""
            SELECT financial_profile_id AS FinancialProfileId, customer_id AS CustomerId,
                   revision AS Revision, updated_at AS UpdatedAt
            FROM financial_profile.profiles WHERE financial_profile_id=@Id;
            """, new { Id = request.Payload.FinancialProfileId }, transaction,
            cancellationToken: cancellationToken));
        if (currentProfile is not null && currentProfile.CustomerId != request.Payload.CustomerId)
            throw new InvalidOperationException("Financial Profile ownership conflict.");

        var existing = (await connection.QueryAsync<FinancialFactRecord>(new CommandDefinition($"""
            SELECT DISTINCT ON (financial_fact_id) {FactColumns}
            FROM financial_profile.fact_revisions WHERE financial_profile_id=@Id
            ORDER BY financial_fact_id, revision DESC;
            """, new { Id = request.Payload.FinancialProfileId }, transaction,
            cancellationToken: cancellationToken))).ToDictionary(
                item => BusinessKey(item.FinancialProfileId, item.FactType, item.Label), StringComparer.Ordinal);

        var savedFacts = new List<FinancialFactRecord>();
        var savedProvenance = new List<FinancialProvenance>();
        var events = new List<object>();
        var createdIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in request.Payload.Facts)
        {
            var key = BusinessKey(request.Payload.FinancialProfileId, source.FactType, source.Label);
            var exists = existing.TryGetValue(key, out var current);
            var revision = exists ? current!.Revision + 1 : 1;
            var factId = exists ? current!.FinancialFactId : StableId("ff", key);
            var provenanceId = StableId("fpv",
                $"{factId}|{source.SourceFactId}|{request.Payload.NormalizationVersion}|{revision}");
            var fact = new FinancialFactRecord(factId, request.Payload.FinancialProfileId,
                request.Payload.CustomerId, source.FactType, source.Label, source.CandidateValue,
                source.Currency, source.EffectiveDate, revision, provenanceId, source.SourceFactId);
            var provenance = new FinancialProvenance(provenanceId, factId, request.Payload.CustomerId,
                source.EvidenceId, source.DocumentVersionId, source.SourceFactId, source.ExtractionVersion,
                source.ValidationVersion, request.Payload.NormalizationVersion, request.Security.Actor.ActorId,
                request.Security.Workload.WorkloadIdentityId, profileChange.OccurredAt,
                request.CorrelationId, request.CausationId);
            savedFacts.Add(fact);
            savedProvenance.Add(provenance);
            if (!exists) createdIds.Add(factId);
            var contractId = exists ? "CID-035" : "CID-034";
            var eventName = exists ? Vs02ContractNames.FinancialFactUpdated : Vs02ContractNames.FinancialFactCreated;
            events.Add(new DomainEvent<FinancialFactChangedPayload>(contractId,
                $"evt-financial-{factId}-{revision}", eventName, ContractGuard.CurrentVersion,
                profileChange.OccurredAt, request.CorrelationId, request.RequestId,
                "Financial Profile Service", "financial-fact", factId,
                new(request.Payload.FinancialProfileId, factId, fact.FactType,
                    profileChange.OccurredAt, provenanceId, revision)));
            existing[key] = fact;
        }

        var profile = new FinancialProfileRecord(request.Payload.FinancialProfileId, request.Payload.CustomerId,
            (currentProfile?.Revision ?? 0) + 1, profileChange.OccurredAt,
            existing.Values.OrderBy(item => item.FactType, StringComparer.Ordinal)
                .ThenBy(item => item.FinancialFactId, StringComparer.Ordinal).ToArray());
        events.Add(profileChange.CreateEvent(profile, savedFacts));

        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO financial_profile.profiles
                (financial_profile_id, customer_id, revision, updated_at)
            VALUES (@FinancialProfileId, @CustomerId, @Revision, @UpdatedAt)
            ON CONFLICT (financial_profile_id) DO UPDATE
            SET revision=EXCLUDED.revision, updated_at=EXCLUDED.updated_at;
            """, profile, transaction, cancellationToken: cancellationToken));
        foreach (var fact in savedFacts)
        {
            await connection.ExecuteAsync(new CommandDefinition($"""
                INSERT INTO financial_profile.fact_revisions
                    (financial_fact_id, revision, financial_profile_id, customer_id, fact_type, label,
                     value, currency, effective_date, financial_provenance_id, source_fact_id)
                VALUES (@FinancialFactId, @Revision, @FinancialProfileId, @CustomerId, @FactType, @Label,
                        @Value, @Currency, @EffectiveDate, @FinancialProvenanceId, @SourceFactId);
                """, fact, transaction, cancellationToken: cancellationToken));
        }
        foreach (var provenance in savedProvenance)
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO financial_profile.provenance
                    (financial_provenance_id, financial_fact_id, customer_id, payload, created_at)
                VALUES (@FinancialProvenanceId, @FinancialFactId, @CustomerId, CAST(@Payload AS jsonb), @RecordedAt);
                """, new
            {
                provenance.FinancialProvenanceId,
                provenance.FinancialFactId,
                provenance.CustomerId,
                Payload = JsonSerializer.Serialize(provenance, ContractJson.Options),
                provenance.RecordedAt
            },
                transaction, cancellationToken: cancellationToken));
        }

        var result = new FinancialNormalizationSave(savedFacts, savedProvenance, events, profile, createdIds, true);
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO financial_profile.idempotency_operations
                (contract_name, contract_version, customer_id, idempotency_key, payload_fingerprint,
                 result_payload, created_at)
            VALUES (@ContractName, @ContractVersion, @CustomerId, @IdempotencyKey, @Fingerprint,
                    CAST(@Result AS jsonb), @CreatedAt);
            """, new
        {
            idempotencyIdentity.ContractName,
            idempotencyIdentity.ContractVersion,
            idempotencyIdentity.CustomerId,
            idempotencyIdentity.IdempotencyKey,
            Fingerprint = fingerprint,
            Result = JsonSerializer.Serialize(new NormalizationSnapshot(savedFacts.ToArray(), savedProvenance.ToArray(),
                    events.Select(item => JsonSerializer.SerializeToElement(item, ContractJson.Options)).ToArray(),
                    profile, createdIds.ToArray(), true), ContractJson.Options),
            CreatedAt = profileChange.OccurredAt
        },
            transaction, cancellationToken: cancellationToken));
        foreach (var governedEvent in events)
            await InsertOutboxAsync(connection, transaction, governedEvent, profileChange.OccurredAt, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<FinancialProfileRecord?> GetProfileAsync(string financialProfileId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<ProfileRow>(new CommandDefinition("""
            SELECT financial_profile_id AS FinancialProfileId, customer_id AS CustomerId,
                   revision AS Revision, updated_at AS UpdatedAt
            FROM financial_profile.profiles WHERE financial_profile_id=@id;
            """, new { id = financialProfileId }, cancellationToken: cancellationToken));
        if (row is null) return null;
        var facts = await connection.QueryAsync<FinancialFactRecord>(new CommandDefinition($"""
            SELECT DISTINCT ON (financial_fact_id) {FactColumns}
            FROM financial_profile.fact_revisions WHERE financial_profile_id=@id
            ORDER BY financial_fact_id, revision DESC;
            """, new { id = financialProfileId }, cancellationToken: cancellationToken));
        return new(row.FinancialProfileId, row.CustomerId, row.Revision,
            new DateTimeOffset(DateTime.SpecifyKind(row.UpdatedAt, DateTimeKind.Utc)),
            facts.OrderBy(item => item.FactType, StringComparer.Ordinal).ThenBy(item => item.FinancialFactId, StringComparer.Ordinal).ToArray());
    }

    public async Task<FinancialFactRecord?> GetFactAsync(string financialFactId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<FinancialFactRecord>(new CommandDefinition($"""
            SELECT {FactColumns} FROM financial_profile.fact_revisions
            WHERE financial_fact_id=@id ORDER BY revision DESC LIMIT 1;
            """, new { id = financialFactId }, cancellationToken: cancellationToken));
    }

    public async Task<FinancialProvenance?> GetProvenanceAsync(string provenanceId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var json = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition("""
            SELECT payload::text FROM financial_profile.provenance WHERE financial_provenance_id=@id;
            """, new { id = provenanceId }, cancellationToken: cancellationToken));
        return json is null ? null : JsonSerializer.Deserialize<FinancialProvenance>(json, ContractJson.Options);
    }

    public ValueTask DisposeAsync() => dataSource.DisposeAsync();

    private static async Task InsertOutboxAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        object value, DateTimeOffset createdAt, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(value, ContractJson.Options));
        var root = document.RootElement;
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO financial_profile.outbox
                (event_id, contract_id, event_name, event_version, occurred_at, correlation_id,
                 causation_id, producer, subject_type, subject_id, payload, created_at)
            VALUES (@EventId, @ContractId, @EventName, @EventVersion, @OccurredAt, @CorrelationId,
                    @CausationId, @Producer, @SubjectType, @SubjectId, CAST(@Payload AS jsonb), @CreatedAt);
            """, new
        {
            EventId = root.GetProperty("eventId").GetString(),
            ContractId = root.GetProperty("contractId").GetString(),
            EventName = root.GetProperty("eventName").GetString(),
            EventVersion = root.GetProperty("eventVersion").GetString(),
            OccurredAt = root.GetProperty("occurredAt").GetDateTimeOffset(),
            CorrelationId = root.GetProperty("correlationId").GetString(),
            CausationId = root.GetProperty("causationId").ValueKind == JsonValueKind.Null ? null : root.GetProperty("causationId").GetString(),
            Producer = root.GetProperty("producer").GetString(),
            SubjectType = root.GetProperty("subjectType").GetString(),
            SubjectId = root.GetProperty("subjectId").GetString(),
            Payload = root.GetProperty("payload").GetRawText(),
            CreatedAt = createdAt
        },
            transaction, cancellationToken: cancellationToken));
    }

    private static string BusinessKey(string profileId, string factType, string label) => $"{profileId}|{factType}|{label}";
    private static string StableId(string prefix, string value) =>
        $"{prefix}-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..24]}";

    private sealed record IdempotencyRow(string PayloadFingerprint, string ResultPayload);
    private sealed record NormalizationSnapshot(FinancialFactRecord[] Facts,
        FinancialProvenance[] Provenance, JsonElement[] Events, FinancialProfileRecord Profile,
        string[] CreatedFactIds, bool Persisted);
    private sealed class ProfileRow
    {
        public string FinancialProfileId { get; set; } = string.Empty;
        public string CustomerId { get; set; } = string.Empty;
        public int Revision { get; set; }
        public DateTime UpdatedAt { get; set; }
    }
}
