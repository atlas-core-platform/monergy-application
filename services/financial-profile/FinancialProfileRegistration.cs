using Microsoft.Extensions.DependencyInjection;
using Monergy.Platform;
using Monergy.Services.FinancialProfile.Application;
using Monergy.Services.FinancialProfile.Infrastructure;
using Monergy.Contracts;

namespace Monergy.Services.FinancialProfile;

public static class FinancialProfileRegistration
{
    private static IServiceCollection AddAuditDelivery(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        if (!FinancialProfileAuditDeliveryOptions.Selected(configuration)) return services;
        if (!TenantBoundaryOptions.Selected(configuration))
            throw new InvalidOperationException("Financial Profile Audit delivery requires governed tenant bindings.");
        services.AddSingleton<EventDeliveryTelemetry>();
        services.AddSingleton<IEventDeliveryTelemetry>(provider =>
            provider.GetRequiredService<EventDeliveryTelemetry>());
        services.AddSingleton<IGovernedEventTransport<AuditableEvent>>(provider =>
            new ReferenceGovernedEventTransport<AuditableEvent>(
                configuration,
                provider.GetServices<IGovernedEventConsumer<AuditableEvent>>(),
                provider.GetRequiredService<IEventDeliveryTelemetry>()));
        services.AddSingleton<FinancialProfileOutboxDispatcher>();
        services.AddHttpClient<HttpFinancialProfileAuditEventConsumer>(client =>
        {
            client.BaseAddress = FinancialProfileAuditDeliveryOptions.Endpoint(configuration);
            client.Timeout = TimeSpan.FromSeconds(5);
        });
        services.AddTransient<IGovernedEventConsumer<AuditableEvent>>(provider =>
            provider.GetRequiredService<HttpFinancialProfileAuditEventConsumer>());
        return services;
    }


    public static IEndpointRouteBuilder MapD11LocalShutdown(this IEndpointRouteBuilder endpoints,
        IConfiguration configuration)
    {
        var token = PhysicalPersistenceGuard.Require(configuration, "Monergy:D11:ControllerToken");
        endpoints.MapPost("/operations/local/control/stop", (HttpRequest request,
            IHostApplicationLifetime lifetime) =>
        {
            if (!LocalControlToken.IsValid(request.Headers["X-Monergy-Local-Control"].ToString(), token))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            lifetime.StopApplication();
            return Results.Accepted();
        });
        return endpoints;
    }

    public static IServiceCollection AddFinancialProfileReferenceAdapters(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ReferenceAdapterGuard.EnsureAllowed(configuration);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<InMemoryFinancialProfileRepository>();
        services.AddSingleton<IFinancialProfileRepository>(provider => provider.GetRequiredService<InMemoryFinancialProfileRepository>());
        services.AddSingleton<FinancialProfileApplication>();
        services.AddSingleton<ILoanRecordRepository, InMemoryLoanRecords>();
        services.AddSingleton<IInvestmentRecordRepository, InMemoryInvestmentRecords>();
        services.AddSingleton<IPropertyRecordRepository, InMemoryPropertyRecords>();
        services.AddSingleton<IVehicleRecordRepository, InMemoryVehicleRecords>();
        services.AddSingleton<IIncomeRecordRepository, InMemoryIncomeRecords>();
        services.AddSingleton<IncomeRecordsApplication>();
        services.AddSingleton<IExpenseRecordRepository, InMemoryExpenseRecords>();
        services.AddSingleton<ILiabilityRecordRepository, InMemoryLiabilityRecords>();
        services.AddSingleton<ExpenseRecordsApplication>();
        services.AddSingleton<LiabilityRecordsApplication>();
        services.AddSingleton<InvestmentRecordsApplication>();
        services.AddSingleton<PropertyRecordsApplication>();
        services.AddSingleton<VehicleRecordsApplication>();
        services.AddSingleton<AssetSnapshotApplication>();
        services.AddSingleton<LoanRecordsApplication>();
        return services.AddAuditDelivery(configuration);
    }

    public static IServiceCollection AddFinancialProfilePhysicalPersistence(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        PhysicalPersistenceGuard.EnsureAllowed(configuration);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IFinancialProfileRepository, PostgresFinancialProfileRepository>();
        services.AddSingleton<FinancialProfileApplication>();
        services.AddSingleton<ILoanRecordRepository, PostgresLoanRecords>();
        services.AddSingleton<IInvestmentRecordRepository, PostgresInvestmentRecords>();
        services.AddSingleton<IPropertyRecordRepository, PostgresPropertyRecords>();
        services.AddSingleton<IVehicleRecordRepository, PostgresVehicleRecords>();
        services.AddSingleton<IIncomeRecordRepository, PostgresIncomeRecords>();
        services.AddSingleton<IncomeRecordsApplication>();
        services.AddSingleton<IExpenseRecordRepository, PostgresExpenseRecords>();
        services.AddSingleton<ILiabilityRecordRepository, PostgresLiabilityRecords>();
        services.AddSingleton<ExpenseRecordsApplication>();
        services.AddSingleton<LiabilityRecordsApplication>();
        services.AddSingleton<InvestmentRecordsApplication>();
        services.AddSingleton<PropertyRecordsApplication>();
        services.AddSingleton<VehicleRecordsApplication>();
        services.AddSingleton<AssetSnapshotApplication>();
        services.AddSingleton<LoanRecordsApplication>();
        return services.AddAuditDelivery(configuration);
    }
}
