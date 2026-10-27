using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.CustomerIdentity;
using Monergy.Services.CustomerIdentity.Application;

const string serviceName = "Customer & Identity Service";

var builder = WebApplication.CreateBuilder(args);
builder.AddMonergyPlatform(serviceName);
builder.Services.AddHealthChecks();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNameCaseInsensitive = ContractJson.Options.PropertyNameCaseInsensitive;
    options.SerializerOptions.UnmappedMemberHandling = ContractJson.Options.UnmappedMemberHandling;
    options.SerializerOptions.WriteIndented = ContractJson.Options.WriteIndented;
});
var tenantIntegration = TenantAccessIntegration.IsSelected(builder.Configuration);
if (tenantIntegration)
{
    if (!builder.Environment.IsDevelopment()) throw new InvalidOperationException("Tenant integration is Development-only.");
    builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 65_536);
    // LOCAL/UAT intentionally runs in Development, where minimal API binding
    // otherwise throws malformed-body failures instead of returning HTTP 400.
    builder.Services.Configure<Microsoft.AspNetCore.Routing.RouteHandlerOptions>(
        options => options.ThrowOnBadRequest = false);
    builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.RespectRequiredConstructorParameters = true);
    builder.Services.AddTenantSessionAuthority(builder.Configuration);
    builder.Services.AddSingleton<Monergy.Services.CustomerIdentity.Infrastructure.PostgresPersonaInvitationStatus>();
    builder.Services.AddSelfSessions();
    // Tenant administrators need the customer directory independently of the
    // customer-facing onboarding composition. Keeping this registration behind
    // CustomerRelationships made the retained LOCAL profile advertise a route
    // that the owner had never mapped.
    builder.Services.AddSingleton<ICustomerAdministrationRepository, Monergy.Services.CustomerIdentity.Infrastructure.PostgresCustomerAdministration>();
    if (builder.Configuration["Monergy:AccessIntegration:CustomerRelationships"] == "true")
    {
        builder.Services.AddSelfCustomerProfiles();
        builder.Services.AddBasicKyc();
        builder.Services.AddCustomerAccountAgreements(builder.Configuration);
        builder.Services.AddCustomerPan(builder.Configuration);
        builder.Services.AddSingleton<CustomerWorkspaceSetupApplication>();
    }
}
var referenceAdapters = ReferenceAdapterGuard.IsSelected(builder.Configuration);
var customerEmailAccess = tenantIntegration && builder.Services.AddCustomerEmailAccess(builder.Configuration);
var customerRegistration = tenantIntegration && builder.Services.AddCustomerRegistration(builder.Configuration, customerEmailAccess);
var personaCredentials = tenantIntegration && builder.Services.AddPersonaCredentials(
    builder.Configuration, customerEmailAccess, builder.Environment.IsDevelopment());
if (personaCredentials && customerEmailAccess && !customerRegistration)
    builder.Services.AddHostedService<Monergy.Services.CustomerIdentity.Infrastructure.CustomerRegistrationDelivery>();
if (referenceAdapters)
{
    builder.Services.AddCustomerIdentityReferenceAdapters(builder.Configuration);
}

var app = builder.Build();
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = static _ => false });
app.MapHealthChecks("/health/ready");
if (referenceAdapters)
{
    app.MapCustomerIdentityContracts();
}

if (tenantIntegration)
{
    app.MapTenantSessionAuthority();
    app.MapPersonaInvitationStatus();
    app.MapSelfSessions();
    app.MapCustomerEmailAccess(customerEmailAccess);
    app.MapCustomerRegistration(customerRegistration);
    app.MapPersonaCredentials(personaCredentials, customerEmailAccess);
    app.MapCustomerAdministration();
    if (app.Configuration["Monergy:AccessIntegration:CustomerRelationships"] == "true")
    {
        app.MapSelfCustomerProfile();
        app.MapBasicKyc();
        app.MapCustomerAccountAgreements();
        app.MapCustomerPan();
    }
}

await app.RunAsync();

public partial class Program;

