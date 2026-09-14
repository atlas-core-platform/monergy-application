using Microsoft.Extensions.DependencyInjection;
using Monergy.Platform;
using Monergy.Services.JobManagement.Application;
using Monergy.Services.JobManagement.Infrastructure;

namespace Monergy.Services.JobManagement;

public static class JobManagementRegistration
{
    public static IServiceCollection AddJobManagementReferenceAdapters(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ReferenceAdapterGuard.EnsureAllowed(configuration);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IJobRepository, InMemoryJobRepository>();
        services.AddSingleton<JobManagementApplication>();
        return services;
    }
}
