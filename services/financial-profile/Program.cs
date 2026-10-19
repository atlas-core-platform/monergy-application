using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Monergy.Platform;
using Monergy.Services.FinancialProfile;
using Monergy.Services.FinancialProfile.Infrastructure;

const string serviceName = "Financial Profile Service";

var builder = WebApplication.CreateBuilder(args);
builder.AddMonergyPlatform(serviceName);
builder.Services.AddHealthChecks();
var referenceAdapters = ReferenceAdapterGuard.IsSelected(builder.Configuration);
if (referenceAdapters)
{
    builder.Services.AddFinancialProfileReferenceAdapters(builder.Configuration);
}
var physicalPersistence = PhysicalPersistenceGuard.IsSelected(builder.Configuration);
if (physicalPersistence)
{
    builder.Services.AddFinancialProfilePhysicalPersistence(builder.Configuration);
}
if ((referenceAdapters || physicalPersistence) &&
    FinancialProfileAuditDeliveryOptions.Selected(builder.Configuration))
{
    builder.Services.AddHostedService<FinancialProfileOutboxDispatchWorker>();
}

var app = builder.Build();
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = static _ => false });
app.MapHealthChecks("/health/ready");
if (referenceAdapters || physicalPersistence)
{
    app.MapFinancialProfileContracts();
    if (TenantBoundaryOptions.Selected(builder.Configuration)) { app.MapLoanRecords(); app.MapInvestmentRecords(); app.MapIncomeRecords(); }
}
if (physicalPersistence && string.Equals(builder.Configuration["Monergy:D11:Profile"], "persisted-reporting", StringComparison.Ordinal))
{
    app.MapD11LocalShutdown(builder.Configuration);
}

await app.RunAsync();

public partial class Program;
