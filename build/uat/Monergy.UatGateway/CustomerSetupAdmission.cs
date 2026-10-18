using Monergy.Platform;
using Microsoft.AspNetCore.Http;

namespace Monergy.UatGateway;

public static class CustomerSetupAdmission
{
    // Raw contract proxy routes are also browser ingress. Hiding the financial
    // shell alone must not leave that parallel route available before setup.
    public static async Task RequireAsync(HttpContext http, CustomerOwnerClient owners)
    {
        var session = await owners.SessionAsync(http).ConfigureAwait(false);
        _ = await owners.OwnedCustomerAsync(session.TenantId, session.ActorId, http.RequestAborted).ConfigureAwait(false)
            ?? throw new TenantBoundaryException("PRODUCT_CUSTOMER_REQUIRED", 403);
    }
}
