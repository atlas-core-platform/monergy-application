using Microsoft.AspNetCore.Http;
using Monergy.Platform;

namespace Monergy.UatGateway;

public static class GatewayExceptionBoundary
{
    public static async Task InvokeAsync(HttpContext http, RequestDelegate next)
    {
        try
        {
            await next(http).ConfigureAwait(false);
        }
        catch (TenantBoundaryException failure)
        {
            if (http.Response.HasStarted) throw;
            http.Response.StatusCode = failure.Status;
            await http.Response.WriteAsJsonAsync(new { error = failure.Code }, http.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is HttpRequestException or TaskCanceledException && !http.RequestAborted.IsCancellationRequested)
        {
            if (http.Response.HasStarted) throw;
            http.Response.StatusCode = 503;
            await http.Response.WriteAsJsonAsync(new { error = "OWNER_UNAVAILABLE" }, http.RequestAborted).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            if (http.Response.HasStarted) throw;
            http.Response.Clear();
            http.Response.StatusCode = 500;
            await http.Response.WriteAsJsonAsync(new { error = "GATEWAY_UNEXPECTED_FAILURE" }, http.RequestAborted).ConfigureAwait(false);
        }
    }
}
