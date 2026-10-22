using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Monergy.Platform;
using Monergy.Services.DocumentIntelligence.Application;
using Npgsql;
namespace Monergy.Services.DocumentIntelligence.Infrastructure;

public sealed class PostgresDocumentClassificationRepository : IDocumentClassificationRepository, IHealthCheck, IAsyncDisposable
{
    private readonly Dictionary<string, NpgsqlDataSource> sources = new(StringComparer.Ordinal);
    public PostgresDocumentClassificationRepository(IConfiguration configuration)
    {
        PhysicalPersistenceGuard.EnsureAllowed(configuration);
        var bindings = configuration.GetSection("Monergy:Classification:RuntimeConnections").GetChildren().ToArray();
        var databases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (bindings.Length == 0) throw new InvalidOperationException("Classification requires tenant database connections.");
        foreach (var binding in bindings)
        {
            if (!TenantAccessClient.Identifier(binding.Key, 64) || string.IsNullOrWhiteSpace(binding.Value)) throw new InvalidOperationException("Invalid classification database binding.");
            var db = new NpgsqlConnectionStringBuilder(binding.Value);
            if (string.IsNullOrWhiteSpace(db.Database) || !databases.Add($"{db.Host}:{db.Port}/{db.Database}")) throw new InvalidOperationException("Classification tenants require distinct databases.");
        }
        foreach (var binding in bindings) sources.Add(binding.Key, NpgsqlDataSource.Create(binding.Value!));
    }
    private NpgsqlDataSource Source(string tenant) => sources.GetValueOrDefault(tenant) ?? throw new TenantBoundaryException("CLASSIFICATION_TENANT_UNAVAILABLE", 403);
    private static void Identity(NpgsqlCommand command, string tenant, string customer, string version)
    { command.Parameters.AddWithValue("tenant", tenant); command.Parameters.AddWithValue("key", ClassificationRules.Key(customer, version)); }
    private static DocumentClassification Decode(string json) => JsonSerializer.Deserialize<DocumentClassification>(json) ?? throw new InvalidOperationException("Invalid classification record.");
    public async Task<DocumentClassification> RecordAsync(DocumentClassification candidate, CancellationToken ct)
    {
        await using var connection = await Source(candidate.TenantId).OpenConnectionAsync(ct);
        await using var insert = new NpgsqlCommand("INSERT INTO document_classifications(tenant_id,classification_key,record,needs_review) VALUES (@tenant,@key,@record::jsonb,@unknown) ON CONFLICT DO NOTHING", connection);
        Identity(insert, candidate.TenantId, candidate.CustomerId, candidate.DocumentVersionId);
        insert.Parameters.AddWithValue("record", JsonSerializer.Serialize(candidate)); insert.Parameters.AddWithValue("unknown", candidate.NeedsReview);
        await insert.ExecuteNonQueryAsync(ct);
        var saved = await ReadAsync(candidate.TenantId, candidate.CustomerId, candidate.DocumentVersionId, ct) ?? throw new InvalidOperationException("Classification commit unavailable.");
        if (saved.SourceHash != candidate.SourceHash) throw new EvidenceDependencyException("Classification source changed.");
        return saved;
    }
    public async Task<DocumentClassification?> ReadAsync(string tenant, string customer, string version, CancellationToken ct)
    {
        await using var command = Source(tenant).CreateCommand("SELECT record::text FROM document_classifications WHERE tenant_id=@tenant AND classification_key=@key"); Identity(command, tenant, customer, version);
        var json = await command.ExecuteScalarAsync(ct) as string; if (json is null) return null;
        var row = Decode(json); Verify(row, tenant, customer, version); return row;
    }
    public async Task<IReadOnlyList<DocumentClassification>> ListAsync(string tenant, bool unknownOnly, string? after, CancellationToken ct)
    {
        await using var command = Source(tenant).CreateCommand("SELECT record::text FROM document_classifications WHERE tenant_id=@tenant AND (NOT @unknown OR needs_review) AND classification_key>@after ORDER BY classification_key COLLATE \"C\" LIMIT 51");
        command.Parameters.AddWithValue("tenant", tenant); command.Parameters.AddWithValue("unknown", unknownOnly); command.Parameters.AddWithValue("after", after ?? "");
        await using var reader = await command.ExecuteReaderAsync(ct); var result = new List<DocumentClassification>();
        while (await reader.ReadAsync(ct)) result.Add(Decode(reader.GetString(0))); return result;
    }
    public async Task<DocumentClassification> ReviewAsync(string tenant, string customer, string version, string actor, ClassificationChange change, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await Source(tenant).OpenConnectionAsync(ct); await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var read = new NpgsqlCommand("SELECT record::text FROM document_classifications WHERE tenant_id=@tenant AND classification_key=@key FOR UPDATE", connection, transaction); Identity(read, tenant, customer, version);
        var json = await read.ExecuteScalarAsync(ct) as string ?? throw new TenantBoundaryException("CLASSIFICATION_NOT_FOUND", 404);
        var current = Decode(json); Verify(current, tenant, customer, version); var next = ClassificationRules.Apply(current, actor, change, now);
        await using var update = new NpgsqlCommand("UPDATE document_classifications SET record=@record::jsonb,needs_review=@unknown WHERE tenant_id=@tenant AND classification_key=@key", connection, transaction);
        Identity(update, tenant, customer, version); update.Parameters.AddWithValue("record", JsonSerializer.Serialize(next)); update.Parameters.AddWithValue("unknown", next.NeedsReview);
        if (await update.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("Classification update unavailable.");
        await transaction.CommitAsync(ct); return next;
    }
    private static void Verify(DocumentClassification row, string tenant, string customer, string version)
    { if (row.TenantId != tenant || row.CustomerId != customer || row.DocumentVersionId != version) throw new InvalidOperationException("Classification identity mismatch."); }
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try { foreach (var source in sources.Values) { await using var command = source.CreateCommand("SELECT tenant_id,classification_key,record,needs_review FROM document_classifications LIMIT 0"); await command.ExecuteNonQueryAsync(deadline.Token); } return HealthCheckResult.Healthy(); }
        catch (Exception e) when (e is not OutOfMemoryException and not StackOverflowException) { return HealthCheckResult.Unhealthy("Configured classification database is unavailable."); }
    }
    public async ValueTask DisposeAsync() { foreach (var source in sources.Values) await source.DisposeAsync(); }
}
