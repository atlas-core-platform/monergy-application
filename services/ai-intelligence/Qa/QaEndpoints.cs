using Monergy.Contracts;
using Monergy.Platform;
namespace Monergy.Services.AiIntelligence.Qa;
public static class QaEndpoints
{
    public static void AddFinancialQa(this WebApplicationBuilder builder)
    {
        if (builder.Configuration["Monergy:FinancialQa:Enabled"] != "true") return;
        if (!builder.Environment.IsDevelopment()) throw new InvalidOperationException("Financial Q&A requires LOCAL/CI Development.");
        TenantAccessIntegration.EnsureAllowed(builder.Configuration);
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 16384);
        builder.Services.AddSingleton<CustomerOwnerClient>(); builder.Services.AddSingleton<TenantAccessClient>();
        builder.Services.AddSingleton(provider => new QaOwners(provider.GetRequiredService<CustomerOwnerClient>(), provider.GetRequiredService<TenantAccessClient>(),
            provider.GetRequiredService<IConfiguration>(), new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false })
            { Timeout = TimeSpan.FromSeconds(40), MaxResponseContentBufferSize = 262144 }));
        builder.Services.AddSingleton<IQaOwners>(provider => provider.GetRequiredService<QaOwners>());
        builder.Services.AddSingleton<QaStore>(); builder.Services.AddSingleton<QaApplication>();
    }
    public static void MapFinancialQa(this WebApplication app)
    {
        if (app.Configuration["Monergy:FinancialQa:Enabled"] != "true") return;
        var group = app.MapGroup("/local/v1/financial-qa");
        group.AddEndpointFilter(CustomerReviewHttp.Filter);
        group.MapGet("/configuration", async (HttpContext http, QaOwners owners) => { CustomerReviewHttp.NoQuery(http.Request); await owners.Authorize(http); return await owners.Configuration(http); });
        group.MapGet("/history", async (HttpContext http, QaApplication service) => { CustomerReviewHttp.NoQuery(http.Request); return await service.History(http); });
        group.MapPost("/questions", async (HttpContext http, QaApplication service) => { CustomerReviewHttp.NoQuery(http.Request); return await service.Ask(http, await CustomerReviewHttp.ReadAsync<AskFinancialQuestion>(http)); });
    }
}
