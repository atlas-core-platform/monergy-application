using System.Text.Json;
using Dapper;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.Audit.Application;
using Npgsql;

namespace Monergy.Services.Audit.Infrastructure;

public sealed class PostgresTenantAccessAudit : ITenantAccessAuditRepository, IAsyncDisposable
{
    private readonly Dictionary<string, NpgsqlDataSource> sources = new(StringComparer.Ordinal);

    public PostgresTenantAccessAudit(IConfiguration configuration)
    {
        TenantAccessIntegration.EnsureAllowed(configuration);
        foreach (var entry in configuration.GetSection("Monergy:AccessIntegration:AuditDatabases").GetChildren())
        {
            TenantAccessProtocol.Identifier(entry.Key, 64);
            var options = new NpgsqlConnectionStringBuilder(entry.Value ?? "")
            {
                IncludeErrorDetail = false,
                NoResetOnClose = false,
                Timeout = 5,
                CommandTimeout = 15,
                MaxPoolSize = 20,
            };
            sources.Add(entry.Key, NpgsqlDataSource.Create(options.ConnectionString));
        }
        if (sources.Count == 0) throw new InvalidOperationException("Tenant Audit bindings are required.");
    }

    public async Task<AccessManagementReceipt> AppendAsync(AccessManagementEvent source, CancellationToken cancellationToken)
    {
        TenantAccessProtocol.Validate(source, source.TenantId, "audit");
        var hash = TenantAccessProtocol.Hash(source);
        await using var connection = await OpenAsync(source.TenantId, cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(hashtextextended(@Key,0));",
            new { Key = source.EventId.ToString("D") }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        var previous = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition("""
            SELECT receipt::text FROM audit.access_event_inbox WHERE event_id=@EventId;
            """, source, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (previous is not null)
        {
            var receipt = JsonSerializer.Deserialize<AccessManagementReceipt>(previous, TenantAccessProtocol.Json)!;
            if (receipt.EventHash != hash) throw new TenantAccessException("EVENT_ID_CONTENT_CONFLICT");
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return receipt with { Disposition = "Duplicate" };
        }
        var evidenceId = "audit-am-" + source.TenantId + "-" + source.EventId.ToString("N");
        var applied = new AccessManagementReceipt("audit", source.TenantId, source.EventId, source.ContractId, source.EventVersion,
            hash, evidenceId, "Applied");
        var parameters = new
        {
            source.EventId,
            SourceEventId = "am-" + source.EventId.ToString("D"),
            EvidenceId = evidenceId,
            source.ContractId,
            source.EventName,
            source.Producer,
            source.AggregateId,
            source.OccurredAt,
            source.CorrelationId,
            Hash = hash,
            Envelope = JsonSerializer.Serialize(source, TenantAccessProtocol.Json),
            Receipt = JsonSerializer.Serialize(applied, TenantAccessProtocol.Json),
        };
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO audit.inbox(source_event_id,received_at) VALUES (@SourceEventId,clock_timestamp());
            INSERT INTO audit.evidence(audit_evidence_id,source_contract_id,source_event_id,event_name,producer,
                subject_type,subject_id,occurred_at,correlation_id,causation_id,recorded_at)
            VALUES (@EvidenceId,@ContractId,@SourceEventId,@EventName,@Producer,'TenantAccess',@AggregateId,@OccurredAt,@CorrelationId,NULL,clock_timestamp());
            INSERT INTO audit.access_event_inbox(event_id,event_hash,envelope,receipt,audit_evidence_id)
            VALUES (@EventId,@Hash,CAST(@Envelope AS jsonb),CAST(@Receipt AS jsonb),@EvidenceId);
            """, parameters, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return applied;
    }

    public async Task<bool> ReadyAsync(CancellationToken cancellationToken)
    {
        try
        {
            foreach (var tenant in sources.Keys)
            {
                await using var connection = await OpenAsync(tenant, cancellationToken).ConfigureAwait(false);
            }
            return true;
        }
        catch (Exception exception) when (exception is NpgsqlException or TenantAccessException) { return false; }
    }

    private async Task<NpgsqlConnection> OpenAsync(string tenantId, CancellationToken cancellationToken)
    {
        if (!sources.TryGetValue(tenantId, out var source)) throw new TenantAccessException("TENANT_UNAVAILABLE", 503);
        var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var valid = await connection.ExecuteScalarAsync<bool>(new CommandDefinition("""
                SELECT EXISTS(SELECT 1 FROM audit.access_tenant_identity WHERE singleton AND tenant_id=@TenantId AND schema_version=1)
                    AND NOT has_schema_privilege(current_user,'audit','CREATE')
                    AND NOT has_schema_privilege(current_user,'public','CREATE')
                    AND NOT has_database_privilege(current_user,current_database(),'CREATE')
                    AND NOT has_table_privilege(current_user,'audit.access_tenant_identity','INSERT,UPDATE,DELETE,TRUNCATE')
                    AND NOT has_table_privilege(current_user,'audit.access_event_inbox','UPDATE,DELETE,TRUNCATE')
                    AND NOT has_table_privilege(current_user,'audit.inbox','UPDATE,DELETE,TRUNCATE')
                    AND NOT has_table_privilege(current_user,'audit.evidence','UPDATE,DELETE,TRUNCATE')
                    AND NOT EXISTS(SELECT 1 FROM pg_roles WHERE rolname=current_user AND (rolsuper OR rolcreatedb OR rolcreaterole OR rolreplication));
                """, new { TenantId = tenantId }, cancellationToken: cancellationToken)).ConfigureAwait(false);
            if (!valid) throw new TenantAccessException("TENANT_BINDING_INVALID", 503);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var source in sources.Values) await source.DisposeAsync().ConfigureAwait(false);
    }
}
