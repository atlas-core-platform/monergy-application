using Monergy.Platform;
using Monergy.Services.Evidence.Application;
using Npgsql;

namespace Monergy.Services.Evidence.Infrastructure;

// Product persistence uses a distinct operator-configured database per tenant.
// The request's normal owner/access boundary authorizes before repository access.
public sealed class TenantEvidenceRepository : IEvidenceRepository, IDocumentVaultRepository, IDocumentMonitoringRepository, IAsyncDisposable
{
    private readonly IHttpContextAccessor context;
    private readonly Dictionary<string, PostgresEvidenceRepository> stores = new(StringComparer.Ordinal);
    public bool RequireScannedUploads => true;
    public string AdapterKind => "TENANT_POSTGRESQL_DURABLE";
    public TenantEvidenceRepository(IConfiguration configuration, IHttpContextAccessor context)
    {
        PhysicalPersistenceGuard.EnsureAllowed(configuration); this.context = context;
        var bindings = configuration.GetSection("Monergy:Persistence:Evidence:RuntimeConnections").GetChildren().ToArray();
        if (bindings.Length == 0) throw new InvalidOperationException("Product Evidence requires tenant database connections.");
        var databases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var binding in bindings)
        {
            if (!TenantAccessClient.Identifier(binding.Key, 64) || string.IsNullOrWhiteSpace(binding.Value))
                throw new InvalidOperationException("Invalid Evidence tenant database binding.");
            var db = new NpgsqlConnectionStringBuilder(binding.Value);
            if (string.IsNullOrWhiteSpace(db.Database) || !databases.Add($"{db.Host}:{db.Port}/{db.Database}"))
                throw new InvalidOperationException("Evidence tenants require distinct databases.");
        }
        foreach (var binding in bindings)
        {
            var own = new ConfigurationBuilder().AddConfiguration(configuration).AddInMemoryCollection(new Dictionary<string, string?>
                { ["Monergy:Persistence:Evidence:RuntimeConnection"] = binding.Value }).Build();
            stores.Add(binding.Key, new PostgresEvidenceRepository(own));
        }
    }
    private PostgresEvidenceRepository Current(string? expected = null)
    {
        var values = context.HttpContext?.Request.Headers["X-Monergy-Tenant"];
        var tenant = values is { Count: 1 } ? values.Value[0] : null;
        if (tenant is null || expected is not null && expected != tenant || !stores.TryGetValue(tenant, out var store))
            throw new TenantBoundaryException("EVIDENCE_TENANT_UNAVAILABLE", 403);
        return store;
    }
    public Task<EvidenceSaveResult> SaveVersionAsync(EvidenceOperationIdentity identity, EvidenceVersionRecord candidate,
        Func<EvidenceVersionRecord, IReadOnlyList<object>> eventFactory, CancellationToken cancellationToken) =>
        Current(candidate.TenantId ?? "").SaveVersionAsync(identity, candidate, eventFactory, cancellationToken);
    public Task<IReadOnlyList<EvidenceVersionRecord>> GetDocumentAsync(string documentId, CancellationToken cancellationToken) => Current().GetDocumentAsync(documentId, cancellationToken);
    public Task<EvidenceVersionRecord?> GetVersionAsync(string versionId, CancellationToken cancellationToken) => Current().GetVersionAsync(versionId, cancellationToken);
    public Task<IReadOnlyList<EvidenceVersionRecord>> ListCustomerAsync(string customerId, int limit, CancellationToken cancellationToken, EvidenceVersionRecord? after = null) =>
        Current().ListCustomerAsync(customerId, limit, cancellationToken, after);
    public Task<VaultPage> VaultListAsync(VaultScope scope, string category, string search, string? after, CancellationToken ct) =>
        Current(scope.TenantId).VaultListAsync(scope, category, search, after, ct);
    public Task<VaultItem?> VaultReadAsync(VaultScope scope, string versionId, CancellationToken ct) => Current(scope.TenantId).VaultReadAsync(scope, versionId, ct);
    public Task<VaultMetadata> VaultSaveAsync(VaultScope scope, string versionId, string actor, VaultMetadataChange change, CancellationToken ct) =>
        Current(scope.TenantId).VaultSaveAsync(scope, versionId, actor, change, ct);
    public Task<DocumentMonitorPage> MonitorAsync(DocumentMonitorScope scope, DocumentMonitorQuery query, CancellationToken ct) =>
        Current(scope.TenantId).MonitorAsync(scope, query, ct);
    public Task<MonitoredDocument?> MonitorReadAsync(DocumentMonitorScope scope, string versionId, CancellationToken ct) => Current(scope.TenantId).MonitorReadAsync(scope, versionId, ct);
    public async Task CheckReadyAsync(CancellationToken ct) { foreach (var store in stores.Values) await store.CheckReadyAsync(ct); }
    public async ValueTask DisposeAsync() { foreach (var store in stores.Values) await store.DisposeAsync(); }
}
