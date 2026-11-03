using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Monergy.Platform;
using Monergy.UatGateway;
using Xunit;

namespace Monergy.FinancialProfile.Tests;

public sealed class UatGatewayHttpClientLifetimeTests
{
    [Fact]
    public async Task ReadinessThenAffectedRoutesAndRepeatKeepDedicatedClientsReusable()
    {
        var handler = new SuccessHandler();
        using var shared = new HttpClient(handler)
        { Timeout = TimeSpan.FromSeconds(7), MaxResponseContentBufferSize = 123456 };
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(ConfigurationValues());
        builder.Services.AddSingleton(shared);
        builder.AddProductInvestmentRecords();
        builder.AddProductLoanRecords();
        builder.AddProductMonthlyCashflow();
        await using var app = builder.Build();
        app.MapGet("/health/ready", async (HttpClient client, CancellationToken cancellationToken) =>
        {
            var states = await Topology.ReadAsync(client, cancellationToken).ConfigureAwait(false);
            return states.All(state => state.Available) ? Results.Ok() : Results.StatusCode(503);
        });
        app.MapGet("/product-api/v1/investment-records", (ProductInvestmentRecordClient _) => Results.Ok());
        app.MapGet("/product-api/v1/loan-records", (ProductLoanRecordClient _) => Results.Ok());
        app.MapGet("/product-api/v1/monthly-cashflow/positions", (ProductMonthlyCashflowClient _) => Results.Ok());
        await app.StartAsync();

        using var browser = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/health/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/product-api/v1/investment-records")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/product-api/v1/loan-records")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/product-api/v1/monthly-cashflow/positions")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/product-api/v1/investment-records")).StatusCode);

        Assert.Equal(Topology.Services.Length, handler.Calls);
        Assert.Equal(TimeSpan.FromSeconds(7), shared.Timeout);
        Assert.Equal(123456, shared.MaxResponseContentBufferSize);
    }

    [Fact]
    public async Task CohortClientsUseDedicatedProductionClientsAfterSharedClientHasSent()
    {
        var handler = new SuccessHandler();
        using var shared = new HttpClient(handler)
        { Timeout = TimeSpan.FromSeconds(7), MaxResponseContentBufferSize = 123456 };
        using var sent = await shared.GetAsync("http://127.0.0.1/shared-started");
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.Configuration.AddInMemoryCollection(ConfigurationValues());
        builder.Services.AddSingleton(shared);
        builder.AddProductInvestmentRecords();
        builder.AddProductLoanRecords();
        builder.AddProductMonthlyCashflow();
        await using var app = builder.Build();

        var investment = app.Services.GetRequiredService<ProductInvestmentRecordClient>();
        var loan = app.Services.GetRequiredService<ProductLoanRecordClient>();
        var cashflow = app.Services.GetRequiredService<ProductMonthlyCashflowClient>();

        Assert.NotSame(shared, OwnedClient(investment));
        Assert.NotSame(shared, OwnedClient(loan));
        Assert.NotSame(shared, OwnedClient(cashflow));
        Assert.Equal(TimeSpan.FromSeconds(10), OwnedClient(investment).Timeout);
        Assert.Equal(TimeSpan.FromSeconds(10), OwnedClient(loan).Timeout);
        Assert.Equal(TimeSpan.FromSeconds(30), OwnedClient(cashflow).Timeout);
        Assert.Equal(1048576, OwnedClient(investment).MaxResponseContentBufferSize);
        Assert.Equal(1048576, OwnedClient(loan).MaxResponseContentBufferSize);
        Assert.Equal(33554432, OwnedClient(cashflow).MaxResponseContentBufferSize);
        Assert.Equal(TimeSpan.FromSeconds(7), shared.Timeout);
        Assert.Equal(123456, shared.MaxResponseContentBufferSize);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task InjectableConstructorsDoNotMutateAStartedClient()
    {
        var handler = new SuccessHandler();
        using var shared = new HttpClient(handler)
        { Timeout = TimeSpan.FromSeconds(17), MaxResponseContentBufferSize = 654321 };
        using var sent = await shared.GetAsync("http://127.0.0.1/shared-started");
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(ConfigurationValues()).Build();

        _ = new ProductInvestmentRecordClient(configuration, shared);
        _ = new ProductLoanRecordClient(configuration, shared);
        _ = new ProductMonthlyCashflowClient(configuration, shared);

        Assert.Equal(TimeSpan.FromSeconds(17), shared.Timeout);
        Assert.Equal(654321, shared.MaxResponseContentBufferSize);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task UnexpectedFailureReturnsOnlyStableGenericJson()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Monergy-Session"] = "credential-must-not-escape";
        context.Response.Body = new MemoryStream();

        await GatewayExceptionBoundary.InvokeAsync(context,
            _ => throw new InvalidOperationException("implementation secret and credential-must-not-escape"));

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        using var json = JsonDocument.Parse(body);
        Assert.Equal("GATEWAY_UNEXPECTED_FAILURE", json.RootElement.GetProperty("error").GetString());
        Assert.Single(json.RootElement.EnumerateObject());
        Assert.DoesNotContain("InvalidOperationException", body, StringComparison.Ordinal);
        Assert.DoesNotContain("implementation secret", body, StringComparison.Ordinal);
        Assert.DoesNotContain("credential-must-not-escape", body, StringComparison.Ordinal);
        Assert.DoesNotContain("X-Monergy-Session", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequestCancellationIsNotConvertedToUnexpectedFailure()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var context = new DefaultHttpContext { RequestAborted = cancellation.Token };
        context.Response.Body = new MemoryStream();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GatewayExceptionBoundary.InvokeAsync(
            context, _ => Task.FromCanceled(cancellation.Token)));

        Assert.NotEqual(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.Equal(0, context.Response.Body.Length);
    }

    [Theory]
    [InlineData(401, "SESSION_REQUIRED")]
    [InlineData(403, "ACCESS_DENIED")]
    [InlineData(409, "CONTEXT_CHANGED")]
    public async Task TenantBoundaryMappingsRemainUnchanged(int status, string code)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await GatewayExceptionBoundary.InvokeAsync(context,
            _ => throw new TenantBoundaryException(code, status));

        Assert.Equal(status, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        using var json = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal(code, json.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task DependencyFailureMappingRemainsServiceUnavailable()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await GatewayExceptionBoundary.InvokeAsync(context,
            _ => throw new HttpRequestException("owner detail must stay private"));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        using var json = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal("OWNER_UNAVAILABLE", json.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task StartedResponseIsNotReplacedByASecondResponse()
    {
        var body = new MemoryStream();
        var features = new FeatureCollection();
        features.Set<IHttpResponseFeature>(new StartedResponseFeature(body));
        var context = new DefaultHttpContext(features);
        Assert.True(context.Response.HasStarted);

        await Assert.ThrowsAsync<InvalidOperationException>(() => GatewayExceptionBoundary.InvokeAsync(
            context, _ => throw new InvalidOperationException("after response start")));

        Assert.Equal(0, body.Length);
    }

    private static Dictionary<string, string?> ConfigurationValues() => new()
    {
        ["Monergy:Product:Enabled"] = "true",
        ["Monergy:Product:FinancialProfileUrl"] = "http://127.0.0.1:5001/",
        ["Monergy:Product:FinancialRulesUrl"] = "http://127.0.0.1:5002/",
    };

    private static HttpClient OwnedClient(object owner) =>
        Assert.IsType<HttpClient>(owner.GetType().GetField("client", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(owner));

    private sealed class SuccessHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private sealed class StartedResponseFeature(Stream body) : IHttpResponseFeature
    {
        public int StatusCode { get; set; } = StatusCodes.Status200OK;
        public string? ReasonPhrase { get; set; }
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public Stream Body { get; set; } = body;
        public bool HasStarted => true;
        public void OnStarting(Func<object, Task> callback, object state) { }
        public void OnCompleted(Func<object, Task> callback, object state) { }
    }
}
