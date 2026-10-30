using Monergy.Platform;

namespace Monergy.Services.IntegrationGateway.Connections;

public sealed record ConnectionOverviewOwner(string TenantId, string ActorId, string CustomerId, long CustomerVersion);
public sealed record ConnectionSourceSnapshot(string State, DateTimeOffset? LastConfirmedAt = null);
public sealed record ConnectionSummary(string ProviderCode, string Provider, string State, string ManagePath,
    DateTimeOffset? LastConfirmedAt);
public sealed record ConnectionOverview(string TenantId, string CustomerId, DateTimeOffset GeneratedAt,
    IReadOnlyList<ConnectionSummary> Connections);

public interface IConnectionOverviewOwners
{
    Task<ConnectionOverviewOwner> AuthorizeAsync(HttpContext http);
}

public interface IConnectionSummarySource
{
    string ProviderCode { get; }
    string Provider { get; }
    string ManagePath { get; }
    Task<ConnectionSourceSnapshot> ReadAsync(HttpContext http);
}

public sealed class ConnectionOverviewOwners(CustomerOwnerClient identity, TenantAccessClient access) : IConnectionOverviewOwners
{
    public async Task<ConnectionOverviewOwner> AuthorizeAsync(HttpContext http)
    {
        var session = await identity.SessionAsync(http).ConfigureAwait(false);
        var workspace = await identity.OwnedCustomerAsync(session.TenantId, session.ActorId, http.RequestAborted)
            .ConfigureAwait(false) ?? throw new TenantBoundaryException("CUSTOMER_OWNER_REQUIRED", 403);
        if (!workspace.WorkspaceReady || !workspace.Active || workspace.OwnerActorId != session.ActorId)
            throw new TenantBoundaryException("CUSTOMER_OWNER_REQUIRED", 403);
        var decision = await access.EvaluateCustomerAsync(http, "integration-gateway.connection.manage", workspace.CustomerId)
            .ConfigureAwait(false);
        if (decision.Outcome != "Allow" || decision.ActorId != session.ActorId || decision.AccessBasis != "Self" ||
            decision.CustomerVersion != workspace.Version)
            throw new TenantBoundaryException("ACCESS_DENIED", 403);
        return new(session.TenantId, session.ActorId, workspace.CustomerId, workspace.Version);
    }
}

public sealed class ConnectionOverviewApplication(
    IConnectionOverviewOwners owners,
    IEnumerable<IConnectionSummarySource> sources,
    TimeProvider clock)
{
    private static readonly HashSet<string> States = new(StringComparer.Ordinal)
    {
        "Not configured", "Ready to connect", "Setup in progress", "Connected", "Needs attention", "Unavailable"
    };

    public async Task<ConnectionOverview> GetAsync(HttpContext http)
    {
        var owner = await owners.AuthorizeAsync(http).ConfigureAwait(false);
        var summaries = new List<ConnectionSummary>();
        foreach (var source in sources.OrderBy(item => item.ProviderCode, StringComparer.Ordinal))
        {
            ConnectionSourceSnapshot snapshot;
            try
            {
                snapshot = await source.ReadAsync(http).ConfigureAwait(false);
            }
            catch (TenantBoundaryException failure) when (failure.Status >= 500)
            {
                snapshot = new("Unavailable");
            }
            catch (Exception failure) when (failure is HttpRequestException or TaskCanceledException or InvalidOperationException)
            {
                snapshot = new("Unavailable");
            }

            var state = States.Contains(snapshot.State) ? snapshot.State : "Unavailable";
            summaries.Add(new(source.ProviderCode, source.Provider, state, source.ManagePath,
                state == "Connected" ? snapshot.LastConfirmedAt : null));
        }
        return new(owner.TenantId, owner.CustomerId, clock.GetUtcNow(), summaries);
    }
}

internal static class ConnectionStateProjection
{
    public static ConnectionSourceSnapshot From(string status, DateTimeOffset? refreshedAt) => status switch
    {
        "Not configured" => new("Not configured"),
        "Configured" or "Disconnected" or "Disconnected; Google revocation unconfirmed" or
            "Disconnected; Microsoft revocation unconfirmed" => new("Ready to connect"),
        "Waiting permission" or "Choose folder" => new("Setup in progress"),
        "Connected" => new("Connected", refreshedAt),
        "Needs attention" or "Permission expired" => new("Needs attention"),
        _ => new("Unavailable"),
    };
}

public sealed class GoogleDriveConnectionSummarySource(IServiceProvider services, IConfiguration configuration) : IConnectionSummarySource
{
    public string ProviderCode => "google-drive";
    public string Provider => "Google Drive";
    public string ManagePath => "/connections/google-drive";
    public async Task<ConnectionSourceSnapshot> ReadAsync(HttpContext http)
    {
        if (configuration["Monergy:GoogleDrive:Enabled"] != "true") return new("Not configured");
        var service = services.GetService<GoogleDrive.DriveConnectionApplication>()
            ?? throw new InvalidOperationException("Google Drive connection owner is unavailable.");
        var view = await service.StatusAsync(http).ConfigureAwait(false);
        return ConnectionStateProjection.From(view.Status, view.RefreshedAt);
    }
}

public sealed class OneDriveConnectionSummarySource(IServiceProvider services, IConfiguration configuration) : IConnectionSummarySource
{
    public string ProviderCode => "onedrive";
    public string Provider => "OneDrive";
    public string ManagePath => "/connections/onedrive";
    public async Task<ConnectionSourceSnapshot> ReadAsync(HttpContext http)
    {
        if (configuration["Monergy:OneDrive:Enabled"] != "true") return new("Not configured");
        var service = services.GetService<OneDrive.DriveConnectionApplication>()
            ?? throw new InvalidOperationException("OneDrive connection owner is unavailable.");
        var view = await service.StatusAsync(http).ConfigureAwait(false);
        return ConnectionStateProjection.From(view.Status, view.RefreshedAt);
    }
}
