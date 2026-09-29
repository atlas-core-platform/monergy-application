using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Dapper;
using Microsoft.Extensions.Configuration;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.Evidence.Application;
using Npgsql;

namespace Monergy.Services.Evidence.Infrastructure;

public sealed class PostgresEvidenceRepository : IEvidenceRepository, IAsyncDisposable
{
    private const string VersionColumns = """
        document_id AS DocumentId, document_version_id AS DocumentVersionId,
        evidence_id AS EvidenceId, customer_id AS CustomerId, version AS Version,
        source_name AS SourceName, original_file_name AS OriginalFileName,
        content_type AS ContentType, content_reference AS ContentReference,
        content_sha256 AS ContentSha256, received_at AS ReceivedAt, created_at AS CreatedAt,
        content_length AS ContentLength, content_storage_key AS ContentStorageKey,
        content_storage_version_id AS ContentStorageVersionId
        """;

    private readonly NpgsqlDataSource dataSource;

    public PostgresEvidenceRepository(IConfiguration configuration)
    {
        PhysicalPersistenceGuard.EnsureAllowed(configuration);
        dataSource = NpgsqlDataSource.Create(PhysicalPersistenceGuard.Require(configuration,
            "Monergy:Persistence:Evidence:RuntimeConnection"));
    }

    public string AdapterKind => "POSTGRESQL_DURABLE";

    public async Task<EvidenceSaveResult> SaveVersionAsync(
        EvidenceOperationIdentity identity,
        EvidenceVersionRecord candidate,
        Func<EvidenceVersionRecord, IReadOnlyList<object>> eventFactory,
        CancellationToken cancellationToken)
    {
        var fingerprint = Fingerprint(candidate);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "SELECT pg_advisory_xact_lock(hashtextextended(@DocumentId, 0));",
            new { candidate.DocumentId }, transaction, cancellationToken: cancellationToken));

        var priorOperation = await connection.QuerySingleOrDefaultAsync<OperationRow>(new CommandDefinition("""
            SELECT payload_fingerprint AS PayloadFingerprint, document_version_id AS DocumentVersionId
            FROM evidence.idempotency_operations
            WHERE contract_name=@ContractName AND contract_version=@ContractVersion
              AND customer_id=@CustomerId AND idempotency_key=@IdempotencyKey;
            """, identity, transaction, cancellationToken: cancellationToken));
        if (priorOperation is not null)
        {
            if (!string.Equals(priorOperation.PayloadFingerprint, fingerprint, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("An idempotency identity cannot identify different evidence content.");
            }

            var prior = await FindVersionAsync(connection, transaction, priorOperation.DocumentVersionId, cancellationToken)
                ?? throw new InvalidOperationException("Committed evidence operation is incomplete.");
            await transaction.CommitAsync(cancellationToken);
            return new(prior, false, prior.Version == 1);
        }

        var owner = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition("""
            SELECT customer_id FROM evidence.document_versions
            WHERE document_id=@DocumentId ORDER BY version LIMIT 1;
            """, new { candidate.DocumentId }, transaction, cancellationToken: cancellationToken));
        if (owner is not null && !string.Equals(owner, candidate.CustomerId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Document identity belongs to another customer.");
        }

        var nextVersion = await connection.ExecuteScalarAsync<int>(new CommandDefinition("""
            SELECT COALESCE(MAX(version), 0) + 1 FROM evidence.document_versions WHERE document_id=@DocumentId;
            """, new { candidate.DocumentId }, transaction, cancellationToken: cancellationToken));
        var saved = candidate with { Version = nextVersion };
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO evidence.document_versions
                (document_version_id, document_id, evidence_id, customer_id, version, source_name,
                 original_file_name, content_type, content_reference, content_storage_key,
                 content_storage_version_id, content_length, content_sha256, received_at, created_at)
            VALUES
                (@DocumentVersionId, @DocumentId, @EvidenceId, @CustomerId, @Version, @SourceName,
                 @OriginalFileName, @ContentType, @ContentReference, @ContentStorageKey,
                 @ContentStorageVersionId, @ContentLength, @ContentSha256, @ReceivedAt, @CreatedAt);
            """, saved, transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO evidence.idempotency_operations
                (contract_name, contract_version, customer_id, idempotency_key, payload_fingerprint,
                 document_version_id, created_at)
            VALUES (@ContractName, @ContractVersion, @CustomerId, @IdempotencyKey, @Fingerprint,
                    @DocumentVersionId, @CreatedAt);
            """, new
        {
            identity.ContractName,
            identity.ContractVersion,
            identity.CustomerId,
            identity.IdempotencyKey,
            Fingerprint = fingerprint,
            saved.DocumentVersionId,
            saved.CreatedAt,
        }, transaction, cancellationToken: cancellationToken));

        foreach (var governedEvent in eventFactory(saved))
        {
            await InsertOutboxAsync(connection, transaction, governedEvent, saved.CreatedAt, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new(saved, true, nextVersion == 1);
    }

    public async Task<IReadOnlyList<EvidenceVersionRecord>> GetDocumentAsync(
        string documentId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var values = await connection.QueryAsync<VersionRow>(new CommandDefinition($"""
            SELECT {VersionColumns} FROM evidence.document_versions
            WHERE document_id=@documentId ORDER BY version;
            """, new { documentId }, cancellationToken: cancellationToken));
        return values.Select(ToRecord).ToArray();
    }

    public async Task<EvidenceVersionRecord?> GetVersionAsync(string versionId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await FindVersionAsync(connection, null, versionId, cancellationToken);
    }

    public ValueTask DisposeAsync() => dataSource.DisposeAsync();

    private static async Task<EvidenceVersionRecord?> FindVersionAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, string versionId,
        CancellationToken cancellationToken)
    {
        var row = await connection.QuerySingleOrDefaultAsync<VersionRow>(new CommandDefinition($"""
            SELECT {VersionColumns} FROM evidence.document_versions WHERE document_version_id=@versionId;
            """, new { versionId }, transaction, cancellationToken: cancellationToken));
        return row is null ? null : ToRecord(row);
    }

    private static async Task InsertOutboxAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        object governedEvent, DateTimeOffset createdAt, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(governedEvent, ContractJson.Options));
        var root = document.RootElement;
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO evidence.outbox
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
            CausationId = root.GetProperty("causationId").ValueKind == JsonValueKind.Null
                    ? null : root.GetProperty("causationId").GetString(),
            Producer = root.GetProperty("producer").GetString(),
            SubjectType = root.GetProperty("subjectType").GetString(),
            SubjectId = root.GetProperty("subjectId").GetString(),
            Payload = root.GetProperty("payload").GetRawText(),
            CreatedAt = createdAt,
        }, transaction, cancellationToken: cancellationToken));
    }

    private static string Fingerprint(EvidenceVersionRecord value)
    {
        var material = string.Join('|', value.DocumentId, value.DocumentVersionId, value.EvidenceId,
            value.CustomerId, value.ContentReference, value.ContentSha256);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }

    private sealed record OperationRow(string PayloadFingerprint, string DocumentVersionId);
    private sealed class VersionRow
    {
        public string DocumentId { get; set; } = string.Empty;
        public string DocumentVersionId { get; set; } = string.Empty;
        public string EvidenceId { get; set; } = string.Empty;
        public string CustomerId { get; set; } = string.Empty;
        public int Version { get; set; }
        public string SourceName { get; set; } = string.Empty;
        public string OriginalFileName { get; set; } = string.Empty;
        public string ContentType { get; set; } = string.Empty;
        public string ContentReference { get; set; } = string.Empty;
        public string ContentSha256 { get; set; } = string.Empty;
        public DateTime ReceivedAt { get; set; }
        public DateTime CreatedAt { get; set; }
        public long ContentLength { get; set; }
        public string ContentStorageKey { get; set; } = string.Empty;
        public string ContentStorageVersionId { get; set; } = string.Empty;
    }

    private static EvidenceVersionRecord ToRecord(VersionRow row) => new(row.DocumentId,
        row.DocumentVersionId, row.EvidenceId, row.CustomerId, row.Version, row.SourceName,
        row.OriginalFileName, row.ContentType, row.ContentReference, row.ContentSha256,
        new DateTimeOffset(DateTime.SpecifyKind(row.ReceivedAt, DateTimeKind.Utc)),
        new DateTimeOffset(DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc)),
        row.ContentLength, row.ContentStorageKey, row.ContentStorageVersionId);
}

public sealed record S3StoredContent(
    EvidenceContentDescriptor Descriptor,
    string ObjectKey,
    string ObjectVersionId);

public sealed class S3EvidenceContentStore : IEvidenceContentStore, IDisposable
{
    private readonly AmazonS3Client client;
    private readonly string bucket;

    public S3EvidenceContentStore(IConfiguration configuration)
    {
        PhysicalPersistenceGuard.EnsureAllowed(configuration);
        bucket = PhysicalPersistenceGuard.Require(configuration, "Monergy:Persistence:Evidence:S3:Bucket");
        var credentials = new BasicAWSCredentials(
            PhysicalPersistenceGuard.Require(configuration, "Monergy:Persistence:Evidence:S3:AccessKey"),
            PhysicalPersistenceGuard.Require(configuration, "Monergy:Persistence:Evidence:S3:SecretKey"));
        client = new AmazonS3Client(credentials, new AmazonS3Config
        {
            ServiceURL = PhysicalPersistenceGuard.Require(configuration, "Monergy:Persistence:Evidence:S3:Endpoint"),
            ForcePathStyle = true,
            UseHttp = true,
        });
    }

    public async Task EnsureBucketAsync(CancellationToken cancellationToken = default)
    {
        var buckets = await client.ListBucketsAsync(cancellationToken);
        if (buckets.Buckets is null || !buckets.Buckets.Any(item => string.Equals(item.BucketName, bucket, StringComparison.Ordinal)))
        {
            await client.PutBucketAsync(new PutBucketRequest { BucketName = bucket }, cancellationToken);
        }

        await client.PutBucketVersioningAsync(new PutBucketVersioningRequest
        {
            BucketName = bucket,
            VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Enabled },
        }, cancellationToken);
    }

    public async Task<S3StoredContent> StageAsync(
        Stream content, string contentType, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        await EnsureBucketAsync(cancellationToken);
        await using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        var bytes = buffer.ToArray();
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var reference = $"content-{Guid.NewGuid():N}";
        var objectKey = $"evidence/{reference[8..10]}/{reference}";
        await using var upload = new MemoryStream(bytes, writable: false);
        var response = await client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = objectKey,
            InputStream = upload,
            ContentType = contentType,
            Metadata = { ["monergy-sha256"] = sha256, ["monergy-reference"] = reference },
        }, cancellationToken);
        var versionId = response.VersionId ?? throw new InvalidOperationException("S3 object version identity was not returned.");
        return new(new(reference, sha256, contentType, bytes.LongLength, objectKey, versionId), objectKey, versionId);
    }

    public async Task<EvidenceContentDescriptor?> GetDescriptorAsync(
        string reference, CancellationToken cancellationToken)
    {
        var objectKey = ObjectKey(reference);
        try
        {
            var response = await client.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = bucket,
                Key = objectKey,
            }, cancellationToken);
            var sha256 = response.Metadata["x-amz-meta-monergy-sha256"];
            return new(reference, sha256, response.Headers.ContentType, response.Headers.ContentLength,
                objectKey, response.VersionId);
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<byte[]?> ReadAsync(string reference, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await client.GetObjectAsync(bucket, ObjectKey(reference), cancellationToken);
            await using var buffer = new MemoryStream();
            await response.ResponseStream.CopyToAsync(buffer, cancellationToken);
            return buffer.ToArray();
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public void Dispose() => client.Dispose();

    private static string ObjectKey(string reference)
    {
        if (!reference.StartsWith("content-", StringComparison.Ordinal) || reference.Length < 10)
        {
            throw new ArgumentException("Evidence content reference is not an opaque D09 reference.", nameof(reference));
        }

        return $"evidence/{reference[8..10]}/{reference}";
    }
}
