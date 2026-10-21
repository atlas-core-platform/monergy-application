using System.Data;
using System.Text.Json;
using Dapper;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.Evidence.Application;

namespace Monergy.Services.Evidence.Infrastructure;

public sealed partial class PostgresEvidenceRepository : IDocumentVaultRepository
{
    private static VaultMetadata DecodeMetadata(string? json) => json is null ? VaultMetadata.Empty :
        JsonSerializer.Deserialize<VaultMetadata>(json, ContractJson.Options) ?? throw new InvalidOperationException("Invalid vault metadata.");
    public async Task<VaultItem?> VaultReadAsync(VaultScope scope, string versionId, CancellationToken ct)
    {
        await using var db = await dataSource.OpenConnectionAsync(ct);
        var row = await FindVersionAsync(db, null, versionId, ct);
        if (row is null || !scope.Owns(row)) return null;
        var json = await db.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT metadata::text FROM evidence.vault_metadata WHERE document_version_id=@versionId", new { versionId }, cancellationToken: ct));
        return DocumentVault.View(row, DecodeMetadata(json));
    }
    public async Task<VaultPage> VaultListAsync(VaultScope scope, string category, string search, string? after, CancellationToken ct)
    {
        DocumentVault.Query(category, search, after);
        await using var db = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        EvidenceVersionRecord? cursor = null;
        if (after is not null)
        {
            cursor = await FindVersionAsync(db, tx, after, ct);
            if (cursor is null || !scope.Owns(cursor)) throw new TenantBoundaryException("VAULT_CURSOR_INVALID", 400);
        }
        var rows = (await db.QueryAsync<VersionRow>(new CommandDefinition("SELECT " + VersionColumns + """
             FROM evidence.document_versions WHERE tenant_id=@TenantId AND customer_id=@CustomerId
             AND (@category='all' OR COALESCE((SELECT category FROM evidence.vault_metadata m WHERE m.document_version_id=evidence.document_versions.document_version_id),'Other')=@category)
             AND strpos(lower(original_file_name),lower(@search))>0
             AND (CAST(@AfterId AS text) IS NULL OR (created_at,document_version_id COLLATE "C")<(@AfterAt,@AfterId COLLATE "C"))
             ORDER BY created_at DESC,document_version_id COLLATE "C" DESC LIMIT 51
            """, new { scope.TenantId, scope.CustomerId, category, search, AfterId = cursor?.DocumentVersionId, AfterAt = cursor?.CreatedAt }, tx, cancellationToken: ct))).Select(ToRecord).ToArray();
        var metadata = (await db.QueryAsync<VaultMetadataRow>(new CommandDefinition(
            "SELECT document_version_id AS VersionId, metadata::text AS Json FROM evidence.vault_metadata WHERE document_version_id=ANY(@Ids)",
            new { Ids = rows.Take(50).Select(x => x.DocumentVersionId).ToArray() }, tx, cancellationToken: ct))).ToDictionary(x => x.VersionId, x => DecodeMetadata(x.Json));
        await tx.CommitAsync(ct);
        return new(rows.Take(50).Select(x => DocumentVault.View(x, metadata.GetValueOrDefault(x.DocumentVersionId) ?? VaultMetadata.Empty)).ToArray(), rows.Length > 50 ? rows[49].DocumentVersionId : null);
    }
    public async Task<VaultMetadata> VaultSaveAsync(VaultScope scope, string versionId, string actor, VaultMetadataChange change, CancellationToken ct)
    {
        DocumentVault.Validate(change);
        await using var db = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        // Scope lock serializes operation IDs across versions as well as optimistic metadata updates.
        await db.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(hashtextextended(@Key, 0))",
            new { Key = "vault:" + scope.TenantId + ":" + scope.CustomerId }, tx, cancellationToken: ct));
        var row = await FindVersionAsync(db, tx, versionId, ct);
        if (row is null || !scope.Owns(row)) throw new TenantBoundaryException("VAULT_NOT_FOUND", 404);
        var prior = await db.QuerySingleOrDefaultAsync<VaultOperationRow>(new CommandDefinition("""
            SELECT document_version_id AS VersionId, actor_id AS Actor, change::text AS Change, receipt::text AS Receipt
            FROM evidence.vault_operations WHERE tenant_id=@TenantId AND customer_id=@CustomerId AND operation_id=@OperationId
            """, new { scope.TenantId, scope.CustomerId, change.OperationId }, tx, cancellationToken: ct));
        if (prior is not null)
        {
            if (prior.VersionId != versionId || prior.Actor != actor || JsonSerializer.Deserialize<VaultMetadataChange>(prior.Change, ContractJson.Options) != change)
                throw new TenantBoundaryException("VAULT_OPERATION_CONFLICT", 409);
            await tx.CommitAsync(ct); return DecodeMetadata(prior.Receipt);
        }
        var current = DecodeMetadata(await db.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT metadata::text FROM evidence.vault_metadata WHERE document_version_id=@versionId", new { versionId }, tx, cancellationToken: ct)));
        if (current.Revision != change.ExpectedRevision) throw new TenantBoundaryException("VAULT_METADATA_CHANGED", 409);
        var receipt = new VaultMetadata(change.Category, change.DocumentType, change.DocumentDate, change.FinancialYear, current.Revision + 1, actor, DateTimeOffset.UtcNow);
        var args = new { scope.TenantId, scope.CustomerId, change.OperationId, versionId, actor, change.Category, receipt.Revision,
            Change = JsonSerializer.Serialize(change, ContractJson.Options), Receipt = JsonSerializer.Serialize(receipt, ContractJson.Options) };
        await db.ExecuteAsync(new CommandDefinition("""
            INSERT INTO evidence.vault_metadata(document_version_id,category,revision,metadata)
            VALUES(@versionId,@Category,@Revision,CAST(@Receipt AS jsonb))
            ON CONFLICT(document_version_id) DO UPDATE SET category=EXCLUDED.category,revision=EXCLUDED.revision,metadata=EXCLUDED.metadata;
            INSERT INTO evidence.vault_operations(tenant_id,customer_id,operation_id,document_version_id,actor_id,change,receipt)
            VALUES(@TenantId,@CustomerId,@OperationId,@versionId,@actor,CAST(@Change AS jsonb),CAST(@Receipt AS jsonb));
            """, args, tx, cancellationToken: ct));
        await tx.CommitAsync(ct); return receipt;
    }
    private sealed record VaultMetadataRow(string VersionId, string Json);
    private sealed record VaultOperationRow(string VersionId, string Actor, string Change, string Receipt);
}
