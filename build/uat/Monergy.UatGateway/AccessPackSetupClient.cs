using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Monergy.Platform;

namespace Monergy.UatGateway;

public sealed record SetupAccessPolicyView(
    string State,
    int ChangeCount,
    int RoleChangeCount,
    int PermissionChangeCount,
    int ConflictCount);

public sealed record SetupAccessPolicyAdoption(string RequestId);

internal sealed record OwnerAccessPackStatus(
    string? InstalledVersion,
    string? InstalledIdentity,
    DateTimeOffset? InstalledAt,
    long PolicyVersion,
    string? LatestOperationId,
    string? LatestPlanHash,
    long? FromPolicyVersion,
    long? ToPolicyVersion);

internal sealed record OwnerAccessPackChange(
    string Kind,
    string ObjectId,
    string? Before,
    string? After);

internal sealed record OwnerAccessPackPlan(
    string? InstalledVersion,
    string TargetVersion,
    long PolicyVersion,
    string PlanHash,
    bool AlreadyInstalled,
    IReadOnlyList<OwnerAccessPackChange> Changes,
    IReadOnlyList<string> Conflicts);

internal sealed record OwnerAccessPackAdoptionResult(
    string PackVersion,
    long PolicyVersion,
    string OperationId,
    string PlanHash,
    bool Replayed);

public sealed class AccessPackSetupClient
{
    private const string TargetVersion = "2.0";
    private const string TargetIdentity = "monergy-default-access-pack/2.0";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };

    private readonly HttpClient client;
    private readonly Uri endpoint;

    public AccessPackSetupClient(IConfiguration configuration, HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(client);
        this.client = client;
        endpoint = TenantAccessIntegration.LoopbackRoot(
            configuration["Monergy:TenantAccess:AccessManagementUrl"]
            ?? throw new InvalidOperationException("Access Management URL is required for tenant setup."));
    }

    public async Task<SetupAccessPolicyView> ReadViewAsync(HttpContext http)
    {
        var status = await SendAsync<OwnerAccessPackStatus>(
            http, HttpMethod.Get, "v1/administration/default-access-pack", null).ConfigureAwait(false);
        ValidateStatus(status);

        if (status.InstalledVersion == TargetVersion)
        {
            if (status.InstalledIdentity != TargetIdentity || string.IsNullOrWhiteSpace(status.LatestOperationId))
                throw new TenantBoundaryException("ACCESS_POLICY_RECEIPT_INVALID", 503);
            return new("Ready", 0, 0, 0, 0);
        }

        if (status.InstalledVersion != "1.0")
            return new("Blocked", 0, 0, 0, 1);

        var plan = await ReadPlanAsync(http).ConfigureAwait(false);
        if (plan.PolicyVersion != status.PolicyVersion || plan.TargetVersion != TargetVersion ||
            plan.InstalledVersion != "1.0" || plan.AlreadyInstalled)
            throw new TenantBoundaryException("ACCESS_POLICY_PLAN_INVALID", 503);

        var roleChanges = plan.Changes.Count(change =>
            change.Kind.EndsWith("_ROLE", StringComparison.Ordinal));
        var permissionChanges = plan.Changes.Count(change =>
            change.Kind.EndsWith("_PERMISSION", StringComparison.Ordinal));
        return plan.Conflicts.Count == 0
            ? new("UpdateAvailable", plan.Changes.Count, roleChanges, permissionChanges, 0)
            : new("Customized", plan.Changes.Count, roleChanges, permissionChanges, plan.Conflicts.Count);
    }

    public async Task<SetupAccessPolicyView> AdoptAsync(
        HttpContext http,
        SetupAccessPolicyAdoption request)
    {
        if (!TenantAccessClient.Identifier(request.RequestId, 80))
            throw new TenantBoundaryException("ACCESS_POLICY_REQUEST_INVALID", 400);

        var current = await ReadViewAsync(http).ConfigureAwait(false);
        if (current.State == "Ready") return current;
        if (current.State == "Customized")
            throw new TenantBoundaryException("ACCESS_POLICY_CUSTOMIZED", 409);
        if (current.State != "UpdateAvailable")
            throw new TenantBoundaryException("ACCESS_POLICY_UPDATE_UNAVAILABLE", 409);

        var plan = await ReadPlanAsync(http).ConfigureAwait(false);
        if (plan.Conflicts.Count != 0 || plan.TargetVersion != TargetVersion ||
            plan.InstalledVersion != "1.0" || plan.AlreadyInstalled)
            throw new TenantBoundaryException("ACCESS_POLICY_PLAN_CHANGED", 409);

        var operationId = "setup-access-" + request.RequestId;
        var result = await SendAsync<OwnerAccessPackAdoptionResult>(
            http,
            HttpMethod.Post,
            "v1/administration/default-access-pack/adopt",
            new
            {
                expectedPolicyVersion = plan.PolicyVersion,
                operationId,
                planHash = plan.PlanHash,
            }).ConfigureAwait(false);
        if (result.PackVersion != TargetVersion || result.OperationId != operationId ||
            result.PlanHash != plan.PlanHash || result.PolicyVersion <= plan.PolicyVersion)
            throw new TenantBoundaryException("ACCESS_POLICY_ADOPTION_INVALID", 503);

        var updated = await ReadViewAsync(http).ConfigureAwait(false);
        if (updated.State != "Ready")
            throw new TenantBoundaryException("ACCESS_POLICY_ADOPTION_UNCONFIRMED", 503);
        return updated;
    }

    public static bool AllowsActivation(SetupAccessPolicyView view) =>
        view.State is "Ready" or "Customized";

    private async Task<OwnerAccessPackPlan> ReadPlanAsync(HttpContext http)
    {
        var plan = await SendAsync<OwnerAccessPackPlan>(
            http, HttpMethod.Get, "v1/administration/default-access-pack/plan", null).ConfigureAwait(false);
        if (plan.PolicyVersion < 1 || plan.PlanHash.Length != 64 ||
            plan.PlanHash.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')) ||
            plan.Changes is null || plan.Conflicts is null)
            throw new TenantBoundaryException("ACCESS_POLICY_PLAN_INVALID", 503);
        return plan;
    }

    private static void ValidateStatus(OwnerAccessPackStatus status)
    {
        if (status.PolicyVersion < 1 ||
            status.InstalledVersion is not ("1.0" or "2.0") ||
            status.InstalledIdentity is null ||
            status.InstalledAt is null)
            throw new TenantBoundaryException("ACCESS_POLICY_RECEIPT_INVALID", 503);
        if (status.InstalledVersion == "1.0" &&
            status.InstalledIdentity != "monergy-default-access-pack/1.0")
            throw new TenantBoundaryException("ACCESS_POLICY_RECEIPT_INVALID", 503);
    }

    private async Task<T> SendAsync<T>(
        HttpContext http,
        HttpMethod method,
        string path,
        object? body)
    {
        var (tenant, session) = TenantAccessClient.Headers(http);
        using var request = new HttpRequestMessage(method, new Uri(endpoint, path));
        request.Headers.Add("X-Monergy-Tenant", tenant);
        request.Headers.Add("X-Monergy-Session", session);
        if (body is not null) request.Content = JsonContent.Create(body);
        try
        {
            using var response = await client.SendAsync(request, http.RequestAborted).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var code = response.StatusCode switch
                {
                    HttpStatusCode.Conflict => "ACCESS_POLICY_CONFLICT",
                    HttpStatusCode.Forbidden => "TENANT_ADMIN_REQUIRED",
                    HttpStatusCode.Unauthorized => "TENANT_SESSION_INVALID",
                    _ => "ACCESS_POLICY_OWNER_UNAVAILABLE",
                };
                throw new TenantBoundaryException(code, (int)response.StatusCode is 401 or 403 or 409
                    ? (int)response.StatusCode
                    : 503);
            }

            return await response.Content.ReadFromJsonAsync<T>(Json, http.RequestAborted).ConfigureAwait(false)
                ?? throw new TenantBoundaryException("ACCESS_POLICY_OWNER_RESPONSE_INVALID", 503);
        }
        catch (TenantBoundaryException) { throw; }
        catch (Exception error) when (error is HttpRequestException or JsonException or NotSupportedException)
        {
            throw new TenantBoundaryException("ACCESS_POLICY_OWNER_UNAVAILABLE", 503);
        }
        catch (OperationCanceledException) when (!http.RequestAborted.IsCancellationRequested)
        {
            throw new TenantBoundaryException("ACCESS_POLICY_OWNER_UNAVAILABLE", 503);
        }
    }
}
