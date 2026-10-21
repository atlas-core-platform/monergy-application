using Dapper;
using Monergy.Platform;

namespace Monergy.Services.Consent;

public sealed record ProviderConsentWrite(string ConnectionId, string Operation, long ExpectedVersion, string? FolderId);
public sealed record ProviderConsentView(string ConnectionId, string CustomerId, string Provider, string Purpose,
    string? FolderId, long Version, bool Allowed, DateTimeOffset ExpiresAt);

public sealed partial class PostgresCustomerConsent
{
    public async Task<ProviderConsentView> ProviderAsync(string tenant, CustomerOwnerContext owner, string actor,
        string connectionId, ProviderConsentWrite? write, CancellationToken ct, string provider = "google-drive")
    {
        RequireOwner(tenant, owner, actor, owner.CustomerId);
        if (provider is not ("google-drive" or "onedrive") || !Guid.TryParseExact(connectionId, "N", out _) || write is not null &&
            (write.ConnectionId != connectionId || write.ExpectedVersion < 0 || write.Operation is not ("Granted" or "FolderBound" or "Revoked") ||
             write.FolderId is not null && (write.FolderId.Length is < 1 or > 200 || !write.FolderId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '!')) ||
             write.Operation == "FolderBound" && write.FolderId is null || write.Operation != "FolderBound" && write.FolderId is not null))
            throw new TenantBoundaryException("PROVIDER_CONSENT_INVALID", 400);
        await using var db = await OpenAsync(tenant, ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        // Same owner state lock as delegated consent: all provider mutations and
        // their immutable evidence commit atomically within the tenant database.
        _ = await RevisionAsync(db, tx, write is not null, ct);
        var row = await db.QuerySingleOrDefaultAsync<ProviderRow>(Command("""
            SELECT connection_id AS ConnectionId, customer_id AS CustomerId, owner_actor_id AS ActorId,
                owner_version AS OwnerVersion, folder_id AS FolderId,version AS Version,
                expires_at AS ExpiresAt, revoked_at AS RevokedAt
            FROM consent.provider_grants WHERE connection_id=@Id AND customer_id=@Customer AND provider=@Provider;
            """, new { Id = connectionId, Customer = owner.CustomerId, Provider = provider }, tx, ct));
        if (write?.Operation == "Granted")
        {
            if (row is not null || write.ExpectedVersion != 0) throw new TenantBoundaryException("PROVIDER_CONSENT_CONFLICT", 409);
            await db.ExecuteAsync(Command("""
                INSERT INTO consent.provider_grants(connection_id,customer_id,owner_actor_id,owner_version,provider,purpose,version,expires_at)
                VALUES(@Id,@Customer,@Actor,@OwnerVersion,@Provider,'document-import',1,clock_timestamp()+interval '90 days');
                """, new { Id = connectionId, Customer = owner.CustomerId, Actor = actor, OwnerVersion = owner.Version, Provider = provider }, tx, ct));
        }
        else if (write is not null)
        {
            if (row is null || row.ActorId != actor || row.OwnerVersion != owner.Version || row.Version != write.ExpectedVersion || row.RevokedAt is not null ||
                write.Operation == "FolderBound" && Utc(row.ExpiresAt) <= DateTimeOffset.UtcNow)
                throw new TenantBoundaryException("PROVIDER_CONSENT_CONFLICT", 409);
            await db.ExecuteAsync(Command("""
                UPDATE consent.provider_grants SET version=version+1,folder_id=CASE WHEN @Operation='FolderBound' THEN @Folder ELSE folder_id END,
                    revoked_at=CASE WHEN @Operation='Revoked' THEN clock_timestamp() ELSE revoked_at END WHERE connection_id=@Id AND provider=@Provider;
                """, new { Id = connectionId, write.Operation, Folder = write.FolderId, Provider = provider }, tx, ct));
        }
        if (write is not null)
            await db.ExecuteAsync(Command("""
                INSERT INTO consent.provider_evidence(evidence_id,connection_id,customer_id,actor_id,operation,version)
                VALUES(@Evidence,@Id,@Customer,@Actor,@Operation,@Version);
                """, new { Evidence = Guid.NewGuid().ToString("N"), Id = connectionId, Customer = owner.CustomerId,
                    Actor = actor, write.Operation, Version = write.ExpectedVersion + 1 }, tx, ct));
        row = await db.QuerySingleOrDefaultAsync<ProviderRow>(Command("""
            SELECT connection_id AS ConnectionId,customer_id AS CustomerId,owner_actor_id AS ActorId,owner_version AS OwnerVersion,
                folder_id AS FolderId,version AS Version,expires_at AS ExpiresAt,revoked_at AS RevokedAt
            FROM consent.provider_grants WHERE connection_id=@Id AND customer_id=@Customer AND provider=@Provider;
            """, new { Id = connectionId, Customer = owner.CustomerId, Provider = provider }, tx, ct));
        await tx.CommitAsync(ct);
        return row is null ? new(connectionId, owner.CustomerId, provider, "document-import", null, 0, false, DateTimeOffset.MinValue)
            : new(connectionId, owner.CustomerId, provider, "document-import", row.FolderId, row.Version,
                row.ActorId == actor && row.OwnerVersion == owner.Version && row.RevokedAt is null && Utc(row.ExpiresAt) > DateTimeOffset.UtcNow, Utc(row.ExpiresAt));
    }
    private sealed record ProviderRow(string ConnectionId, string CustomerId, string ActorId, long OwnerVersion,
        string? FolderId, long Version, DateTime ExpiresAt, DateTime? RevokedAt);
}

public static class ProviderConsentEndpoints
{
    public static void MapProviderConsent(this WebApplication app)
    {
        app.MapGet("/local/v1/provider-consents/{provider}/{connectionId}", (string provider, string connectionId, HttpContext http,
            CustomerOwnerClient owners, PostgresCustomerConsent store) => Execute(provider, connectionId, null, http, owners, store));
        app.MapPost("/local/v1/provider-consents/{provider}/{connectionId}", (string provider, string connectionId, ProviderConsentWrite write, HttpContext http,
            CustomerOwnerClient owners, PostgresCustomerConsent store) => Execute(provider, connectionId, write, http, owners, store));
    }
    private static async Task<IResult> Execute(string provider, string id, ProviderConsentWrite? write, HttpContext http, CustomerOwnerClient owners, PostgresCustomerConsent store)
    {
        try
        {
            var session = await owners.SessionAsync(http);
            var workspace = await owners.OwnedCustomerAsync(session.TenantId, session.ActorId, http.RequestAborted)
                ?? throw new TenantBoundaryException("CUSTOMER_OWNER_REQUIRED", 403);
            if (!workspace.WorkspaceReady) throw new TenantBoundaryException("WORKSPACE_SETUP_REQUIRED", 403);
            return Results.Ok(await store.ProviderAsync(session.TenantId,
                new(session.TenantId, workspace.CustomerId, workspace.OwnerActorId, workspace.Version, workspace.Active), session.ActorId, id, write, http.RequestAborted, provider));
        }
        catch (TenantBoundaryException error) { return Results.Json(new { error = error.Code }, statusCode: error.Status); }
        catch (System.Data.Common.DbException) { return Results.Json(new { error = "CONSENT_UNAVAILABLE" }, statusCode: 503); }
    }
}
