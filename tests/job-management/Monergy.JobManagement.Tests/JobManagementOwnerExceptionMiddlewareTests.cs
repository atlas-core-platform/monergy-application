using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Monergy.Platform;
using Monergy.Services.JobManagement;
using Xunit;

namespace Monergy.JobManagement.Tests;

public sealed class JobManagementOwnerExceptionMiddlewareTests
{
    [Fact]
    public async Task TypedOwnerFailurePreservesStatusAndReturnsOnlySanitizedJson()
    {
        var middleware = new JobManagementOwnerExceptionMiddleware(
            _ => throw new TenantBoundaryException("JOB_RESULT_NOT_FOUND", 404));
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
        context.Response.Body.Position = 0;
        using var body = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal(["error"], body.RootElement.EnumerateObject().Select(item => item.Name).ToArray());
        Assert.Equal("JOB_RESULT_NOT_FOUND", body.RootElement.GetProperty("error").GetString());
    }
}
