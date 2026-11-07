using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Monergy.Contracts;
using Monergy.Services.JobManagement;
using Monergy.Services.JobManagement.Application;
using Monergy.Services.JobManagement.Infrastructure;
using Xunit;

namespace Monergy.JobManagement.Tests;

public sealed class PhysicalDocumentProcessingCompositionTests
{
    [Fact]
    public void PhysicalPersistenceRetainsTheOwnerBackedDocumentProcessingTarget()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Monergy:ExecutionZone"] = "CI_EPHEMERAL",
                ["Monergy:Persistence:Provider"] = "POSTGRESQL_S3",
                ["Monergy:Persistence:JobManagement:RuntimeConnection"] =
                    "Host=127.0.0.1;Database=fixture;Username=fixture;Password=fixture",
                ["Monergy:DocumentProcessing:Enabled"] = "true",
            }).Build();
        var services = new ServiceCollection();

        services.AddJobManagementPhysicalPersistence(configuration);

        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(DocumentProcessingJobRegistry));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(HttpDocumentProcessingTarget));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IJobExecutionTarget));
    }
}
