using System.Data;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Monergy.Platform;
using Npgsql;

namespace Monergy.Services.Audit.PlatformAudit;

public static class PlatformAuditModule
{
    public static IServiceCollection AddPlatformAudit(this IServiceCollection services)
    {
        services.AddSingleton<PlatformAuditStore>();
        return services;
    }

    public static void MapPlatformAudit(this WebApplication app)
    {
        var group = app.MapGroup("/internal/platform-audit/v1");
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            try { return await next(context).ConfigureAwait(false); }
            catch (TenantBoundaryException failure) { return Results.Json(new { error = failure.Code }, statusCode: failure.Status); }
            catch (Exception failure) when (failure is System.Data.Common.DbException or InvalidOperationException or OperationCanceledException)
            { return Results.Json(new { error = "PLATFORM_AUDIT_UNAVAILABLE" }, statusCode: 503); }
        });
        group.MapPost("/events", async (HttpContext http, IConfiguration config, PlatformAuditStore store) =>
        {
            var supplied = http.Request.Headers["X-Monergy-Platform-Audit"].ToString();
            var am = config["Monergy:PlatformAudit:AmToken"];
            var ai = config["Monergy:PlatformAudit:FeedbackToken"];
            if (am == ai || !string.Equals(config["Monergy:PlatformAudit:Enabled"], "true", StringComparison.OrdinalIgnoreCase))
                throw new TenantBoundaryException("PLATFORM_AUDIT_UNAVAILABLE", 503);
            var producer = AssistantContract.TokenMatches(am, supplied) ? "am-platform" :
                AssistantContract.TokenMatches(ai, supplied) ? "ai-feedback" : throw new TenantBoundaryException("WORKLOAD_AUTHORIZATION_REQUIRED", 403);
            AssistantContract.RequireWorkload(http, "X-Monergy-Platform-Audit", producer == "am-platform" ? am : ai);
            var envelope = await AssistantContract.ReadAsync<PlatformAuditEnvelope>(http.Request, 8192, http.RequestAborted).ConfigureAwait(false);
            AssistantContract.ValidateAudit(envelope, producer);
            return Results.Ok(await store.AppendAsync(envelope, http.RequestAborted).ConfigureAwait(false));
        });
        group.MapGet("/status", async (HttpContext http, IConfiguration config, PlatformAuditStore store) =>
        {
            if (!string.Equals(config["Monergy:PlatformAudit:Enabled"], "true", StringComparison.OrdinalIgnoreCase) ||
                config["Monergy:PlatformAudit:AmToken"] == config["Monergy:PlatformAudit:FeedbackToken"])
                throw new TenantBoundaryException("PLATFORM_AUDIT_UNAVAILABLE", 503);
            var supplied = http.Request.Headers["X-Monergy-Platform-Audit"].ToString();
            if (!AssistantContract.TokenMatches(config["Monergy:PlatformAudit:AmToken"], supplied) &&
                !AssistantContract.TokenMatches(config["Monergy:PlatformAudit:FeedbackToken"], supplied))
                throw new TenantBoundaryException("WORKLOAD_AUTHORIZATION_REQUIRED", 403);
            AssistantContract.RequireWorkload(http, "X-Monergy-Platform-Audit", AssistantContract.TokenMatches(config["Monergy:PlatformAudit:AmToken"], supplied)
                ? config["Monergy:PlatformAudit:AmToken"] : config["Monergy:PlatformAudit:FeedbackToken"]);
            return await store.ReadyAsync(http.RequestAborted).ConfigureAwait(false)
                ? Results.Ok(new { ready = true }) : Results.Json(new { ready = false }, statusCode: 503);
        });
    }
}

public sealed class PlatformAuditStore(IConfiguration configuration)
{
    private string Connection => configuration["Monergy:PlatformAudit:ConnectionString"] is { Length: > 0 } connection
        ? connection : throw new InvalidOperationException("Platform audit store is not configured.");

    public async Task<bool> ReadyAsync(CancellationToken ct)
    {
        try
        {
            await using var connection = new NpgsqlConnection(Connection);
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var command = new NpgsqlCommand("SELECT to_regclass('audit_platform.receipts') IS NOT NULL", connection);
            return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is true;
        }
        catch (Exception failure) when (failure is System.Data.Common.DbException or InvalidOperationException) { return false; }
    }

    public async Task<PlatformAuditReceipt> AppendAsync(PlatformAuditEnvelope envelope, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(envelope, AssistantContract.Json);
        var digest = AssistantContract.Hash(payload);
        var eventId = Guid.Parse(envelope.EventId);
        await using var connection = new NpgsqlConnection(Connection);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);
        await using (var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@id,0))", connection, transaction))
        { command.Parameters.AddWithValue("id", eventId.ToString()); await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false); }
        await using (var command = new NpgsqlCommand("SELECT i.payload_hash,i.producer,r.receipt_id,r.recorded_at FROM audit_platform.inbox i JOIN audit_platform.receipts r USING(event_id) WHERE event_id=@id", connection, transaction))
        {
            command.Parameters.AddWithValue("id", eventId);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (reader.GetString(0) != digest || reader.GetString(1) != envelope.Producer)
                    throw new TenantBoundaryException("PLATFORM_AUDIT_REPLAY_CONFLICT", 409);
                return new(envelope.EventId, reader.GetGuid(2).ToString(), reader.GetFieldValue<DateTimeOffset>(3), "Accepted");
            }
        }
        var receipt = new PlatformAuditReceipt(envelope.EventId, Guid.NewGuid().ToString(), DateTimeOffset.UtcNow, "Accepted");
        await using (var command = new NpgsqlCommand("""
            INSERT INTO audit_platform.inbox(event_id,producer,payload_hash) VALUES(@id,@producer,@hash);
            INSERT INTO audit_platform.evidence(event_id,producer,realm,envelope) VALUES(@id,@producer,'MONERGY_PLATFORM',@payload::jsonb);
            INSERT INTO audit_platform.receipts(event_id,receipt_id,recorded_at) VALUES(@id,@receipt,@recorded);
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("id", eventId); command.Parameters.AddWithValue("producer", envelope.Producer);
            command.Parameters.AddWithValue("hash", digest); command.Parameters.AddWithValue("payload", payload);
            command.Parameters.AddWithValue("receipt", Guid.Parse(receipt.ReceiptId)); command.Parameters.AddWithValue("recorded", receipt.RecordedAt);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return receipt;
    }
}
