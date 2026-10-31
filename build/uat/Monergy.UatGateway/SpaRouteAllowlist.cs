namespace Monergy.UatGateway;

// Explicit, case-sensitive document navigation. Never serve the SPA shell for
// API endpoints, arbitrary filesystem paths, or unknown route prefixes.
public static class SpaRouteAllowlist
{
    private static readonly HashSet<string> Routes = new(StringComparer.Ordinal)
    {
        "/", "/actions", "/evidence",
        "/access", "/access/users", "/access/users/history", "/access/roles",
        "/access/permissions", "/access/groups", "/access/resources", "/access/activity",
        "/access/audit", "/access/sessions", "/access/customers", "/access/requests", "/access/processing",
        "/access/documents", "/operations",
        "/advisor", "/dsa", "/cases", "/welcome",
        "/kyc", "/kyc/reviews", "/monthly-cashflow", "/net-worth",
        "/financial-qa", "/search", "/reports", "/finances", "/consent",
        "/connections", "/connections/google-drive", "/connections/onedrive", "/profile",
        "/settings/sessions", "/settings/agreements", "/vs02", "/foundation"
    };

    public static bool Contains(string? path) => path is not null && Routes.Contains(path);
}
