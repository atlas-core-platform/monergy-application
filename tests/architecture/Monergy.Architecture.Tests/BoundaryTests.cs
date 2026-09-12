using System.Xml.Linq;
using Monergy.Platform;
using Xunit;

namespace Monergy.Architecture.Tests;

public sealed class BoundaryTests
{
    private static readonly string[] ServiceIds =
    [
        "customer-identity",
        "consent",
        "integration-gateway",
        "evidence",
        "document-intelligence",
        "financial-profile",
        "financial-rules",
        "search-retrieval",
        "ai-intelligence",
        "reporting",
        "job-management",
        "audit",
    ];

    private static readonly Dictionary<string, string> ExpectedSdk =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["customer-identity"] = "Microsoft.NET.Sdk.Web",
            ["consent"] = "Microsoft.NET.Sdk.Web",
            ["integration-gateway"] = "Microsoft.NET.Sdk.Web",
            ["evidence"] = "Microsoft.NET.Sdk.Web",
            ["document-intelligence"] = "Microsoft.NET.Sdk.Worker",
            ["financial-profile"] = "Microsoft.NET.Sdk.Web",
            ["financial-rules"] = "Microsoft.NET.Sdk.Web",
            ["search-retrieval"] = "Microsoft.NET.Sdk.Web",
            ["ai-intelligence"] = "Microsoft.NET.Sdk.Web",
            ["reporting"] = "Microsoft.NET.Sdk.Worker",
            ["job-management"] = "Microsoft.NET.Sdk.Worker",
            ["audit"] = "Microsoft.NET.Sdk.Worker",
        };

    [Fact]
    public void ExactlyTwelveCanonicalServiceProjectsExist()
    {
        var projects = Directory.GetFiles(ServicesRoot, "*.csproj", SearchOption.AllDirectories);

        Assert.Equal(12, projects.Length);
        Assert.Equal(ServiceIds.Order(), projects.Select(ProjectServiceId).Order());
    }

    [Fact]
    public void ServiceHostsMatchTheApprovedPrimaryResponsibilitySplit()
    {
        foreach (var serviceId in ServiceIds)
        {
            var project = LoadServiceProject(serviceId);
            Assert.Equal(ExpectedSdk[serviceId], project.Root?.Attribute("Sdk")?.Value);
        }
    }

    [Fact]
    public void ServicesReferenceOnlyTheSharedTechnicalPlatform()
    {
        foreach (var serviceId in ServiceIds)
        {
            var references = LoadServiceProject(serviceId)
                .Descendants("ProjectReference")
                .Select(element => element.Attribute("Include")?.Value.Replace('\\', '/'))
                .ToArray();

            Assert.Single(references);
            Assert.EndsWith("shared/platform/Monergy.Platform/Monergy.Platform.csproj", references[0], StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(references, reference => reference?.Contains("services/", StringComparison.OrdinalIgnoreCase) == true);
        }
    }

    [Fact]
    public void MigrationHistoriesRemainServiceOwnedAndEmpty()
    {
        foreach (var serviceId in ServiceIds)
        {
            var migrationRoot = Path.Combine(ServicesRoot, serviceId, "migrations");
            Assert.True(Directory.Exists(migrationRoot));
            Assert.DoesNotContain(
                Directory.GetFiles(migrationRoot),
                path => !path.EndsWith(".gitkeep", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void NoDatabaseOrProviderSdkIsSelected()
    {
        var forbidden = new[]
        {
            "EntityFrameworkCore",
            "Npgsql",
            "SqlClient",
            "MongoDB",
            "StackExchange.Redis",
            "Azure.",
            "Amazon.",
            "Google.Cloud",
            "OpenAI",
        };

        foreach (var projectPath in Directory.GetFiles(RepositoryRoot, "*.csproj", SearchOption.AllDirectories))
        {
            var content = File.ReadAllText(projectPath);
            Assert.DoesNotContain(forbidden, value => content.Contains(value, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void PlatformMetadataCannotBeMistakenForAProductFeature()
    {
        var metadata = ComponentMetadata.Create("test-component");

        Assert.Equal(ComponentMetadata.ToolchainOnlyState, metadata.ImplementationState);
        Assert.Equal("test-component", metadata.Component);
    }

    private static XDocument LoadServiceProject(string serviceId) =>
        XDocument.Load(Directory.GetFiles(Path.Combine(ServicesRoot, serviceId), "*.csproj").Single());

    private static string ProjectServiceId(string path) =>
        new DirectoryInfo(Path.GetDirectoryName(path)!).Name;

    private static string ServicesRoot => Path.Combine(RepositoryRoot, "services");

    private static string RepositoryRoot
    {
        get
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current is not null && !File.Exists(Path.Combine(current.FullName, "repository.manifest.json")))
            {
                current = current.Parent;
            }

            return current?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
        }
    }
}
