using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Monergy.Platform;
using Monergy.Platform.ControlPlane;

namespace Monergy.UatGateway;

public enum EntryHostKind { Health, Platform, Tenant }

public sealed record TrustedEntryContext(
    EntryHostKind Kind,
    string Hostname,
    string? TenantId,
    string? OrganizationId,
    string? DisplayName,
    string? TenantSlug)
{
    public const string ItemKey = "Monergy.TrustedEntryContext";

    public static TrustedEntryContext RequireTenant(HttpContext http) =>
        http.Items.TryGetValue(ItemKey, out var value) && value is TrustedEntryContext { Kind: EntryHostKind.Tenant } context
            ? context
            : throw new TenantBoundaryException("TENANT_HOST_REQUIRED", 403);
}

public static class TenantEntryRouting
{
    public static EntryHostKind? ClassifyReservedHost(string hostname, string path) =>
        hostname.ToLowerInvariant() switch
        {
            "127.0.0.1" or "localhost" when path.StartsWith("/health/", StringComparison.Ordinal) => EntryHostKind.Health,
            "platform.localhost" => EntryHostKind.Platform,
            _ => null,
        };

    public static bool AllowsPath(EntryHostKind kind, string path)
    {
        var platformOnly = path.StartsWith("/platform-api/", StringComparison.Ordinal) || path is "/platform" or "/operations" ||
            path.StartsWith("/platform/", StringComparison.Ordinal) || path.StartsWith("/operations/", StringComparison.Ordinal);
        var tenantOnly = path == "/welcome" || path.StartsWith("/identity-api/", StringComparison.Ordinal) ||
            path.StartsWith("/access-api/", StringComparison.Ordinal) || path.StartsWith("/product-api/", StringComparison.Ordinal) ||
            path.StartsWith("/uat-api/", StringComparison.Ordinal) || path.StartsWith("/case-api/", StringComparison.Ordinal) ||
            path.StartsWith("/contracts/", StringComparison.Ordinal) || path is "/access" or "/advisor" or "/dsa" or
            "/lender" or "/financial-professional" or "/property-valuer" or "/legal-advisor" or
            "/operations-support" or "/kyc/reviews" || path.StartsWith("/access/", StringComparison.Ordinal);
        return kind switch
        {
            EntryHostKind.Platform => !tenantOnly,
            EntryHostKind.Tenant => !platformOnly,
            EntryHostKind.Health => path.StartsWith("/health/", StringComparison.Ordinal),
            _ => false,
        };
    }

    public static void ApplyTrustedTenant(HttpContext http, TrustedEntryContext context)
    {
        http.Request.Headers.Remove("X-Tenant-Id");
        http.Request.Headers.Remove("X-Monergy-Tenant");
        if (context.Kind != EntryHostKind.Tenant) return;
        http.Request.Headers["X-Monergy-Tenant"] = context.TenantId!;
        if (!http.Request.Query.Any(item => string.Equals(item.Key, "tenantId", StringComparison.OrdinalIgnoreCase))) return;
        var trustedQuery = http.Request.Query
            .SelectMany(item => string.Equals(item.Key, "tenantId", StringComparison.OrdinalIgnoreCase)
                ? Enumerable.Repeat(new KeyValuePair<string, string?>("tenantId", context.TenantId), 1)
                : item.Value.Select(value => new KeyValuePair<string, string?>(item.Key, value)));
        http.Request.QueryString = QueryString.Create(trustedQuery);
    }

    public static void UseTrustedEntryRouting(this WebApplication app)
    {
        app.Use(async (http, next) =>
        {
            var hostname = http.Request.Host.Host.ToLowerInvariant();
            var path = http.Request.Path.Value ?? "/";
            TrustedEntryContext? context;
            var reserved = ClassifyReservedHost(hostname, path);
            if (reserved == EntryHostKind.Health)
                context = new TrustedEntryContext(EntryHostKind.Health, hostname, null, null, null, null);
            else if (reserved == EntryHostKind.Platform)
                context = new TrustedEntryContext(EntryHostKind.Platform, hostname, null, null, "Monergy Platform", null);
            else if (hostname.EndsWith(".localhost", StringComparison.Ordinal))
            {
                var registry = http.RequestServices.GetRequiredService<ITenantRegistry>();
                var environment = app.Configuration["Monergy:ExecutionZone"] ?? "LOCAL";
                var resolved = await registry.ResolveEndpointAsync(hostname, environment, http.RequestAborted).ConfigureAwait(false);
                context = resolved is null ? null : new TrustedEntryContext(
                    EntryHostKind.Tenant, hostname, resolved.TenantId, resolved.OrganizationId,
                    resolved.DisplayName, resolved.Endpoint.TenantSlug);
            }
            else
                context = null;

            if (context is null)
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                await http.Response.WriteAsJsonAsync(new { error = "UNKNOWN_ENTRY_HOST" }, http.RequestAborted).ConfigureAwait(false);
                return;
            }

            if (!AllowsPath(context.Kind, path))
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            // Browser-supplied tenant identity is never trusted. Only this edge-created
            // value can cross the Gateway boundary.
            ApplyTrustedTenant(http, context);
            http.Items[TrustedEntryContext.ItemKey] = context;
            await next().ConfigureAwait(false);
        });
    }

    public static void MapEntryContext(this WebApplication app)
    {
        app.MapGet("/entry-api/local/v1/context", (HttpContext http) =>
        {
            if (!http.Items.TryGetValue(TrustedEntryContext.ItemKey, out var value) || value is not TrustedEntryContext context)
                return Results.Json(new { error = "ENTRY_CONTEXT_REQUIRED" }, statusCode: 403);
            return Results.Ok(context.Kind == EntryHostKind.Tenant
                ? new
                {
                    kind = "tenant", context.Hostname, context.TenantId, context.OrganizationId,
                    context.DisplayName, context.TenantSlug,
                }
                : new
                {
                    kind = context.Kind == EntryHostKind.Platform ? "platform" : "health",
                    context.Hostname, TenantId = (string?)null, OrganizationId = (string?)null,
                    context.DisplayName, TenantSlug = (string?)null,
                });
        });
    }

    public static byte[] ReplaceTopLevelTenantId(HttpContext http, byte[] body)
    {
        if (!http.Items.TryGetValue(TrustedEntryContext.ItemKey, out var value) ||
            value is not TrustedEntryContext { Kind: EntryHostKind.Tenant } context)
            return body;
        var node = JsonNode.Parse(body) as JsonObject ?? throw new TenantBoundaryException("JSON_OBJECT_REQUIRED", 400);
        var tenantKey = node.Select(item => item.Key)
            .FirstOrDefault(key => string.Equals(key, "tenantId", StringComparison.OrdinalIgnoreCase));
        if (tenantKey is not null)
        {
            node.Remove(tenantKey);
            node["tenantId"] = context.TenantId;
        }
        return JsonSerializer.SerializeToUtf8Bytes(node);
    }
}
