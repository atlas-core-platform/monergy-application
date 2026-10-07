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
            ["reporting"] = "Microsoft.NET.Sdk.Web",
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
    public void ServicesReferenceOnlySharedPlatformAndGovernedContracts()
    {
        static bool IsPlatformReference(string? reference) =>
            reference?.EndsWith("shared/platform/Monergy.Platform/Monergy.Platform.csproj", StringComparison.OrdinalIgnoreCase) == true;
        static bool IsContractsReference(string? reference) =>
            reference?.EndsWith("contracts/Monergy.Contracts/Monergy.Contracts.csproj", StringComparison.OrdinalIgnoreCase) == true;

        foreach (var serviceId in ServiceIds)
        {
            var references = LoadServiceProject(serviceId)
                .Descendants("ProjectReference")
                .Select(element => element.Attribute("Include")?.Value.Replace('\\', '/'))
                .ToArray();

            var platformReferences = references.Where(IsPlatformReference).ToArray();
            var contractsReferences = references.Where(IsContractsReference).ToArray();

            Assert.Single(platformReferences);
            Assert.InRange(contractsReferences.Length, 0, 1);
            Assert.Equal(references.Length, references.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.Equal(1 + contractsReferences.Length, references.Length);
            Assert.All(references, reference =>
                Assert.True(IsPlatformReference(reference) || IsContractsReference(reference)));
            Assert.DoesNotContain(references, reference => reference?.Contains("services/", StringComparison.OrdinalIgnoreCase) == true);
        }
    }

    [Fact]
    public void SqlMigrationsRemainOwnedByTheGovernedPhysicalPersistenceCohorts()
    {
        var persistenceCohort = new HashSet<string>(StringComparer.Ordinal)
        {
            "audit",
            "evidence",
            "financial-profile",
            "financial-rules",
            "reporting",
            "job-management",
            "customer-identity", // AR-001 / AM-05 owner-local tenant sessions.
        };
        var authorizedRoots = persistenceCohort
            .Select(serviceId => Path.GetFullPath(Path.Combine(ServicesRoot, serviceId, "migrations")))
            .ToArray();

        foreach (var serviceId in ServiceIds)
        {
            var migrationRoot = Path.Combine(ServicesRoot, serviceId, "migrations");
            Assert.True(Directory.Exists(migrationRoot));
            var sqlMigrations = Directory.GetFiles(migrationRoot, "*.sql", SearchOption.AllDirectories);
            if (persistenceCohort.Contains(serviceId))
            {
                Assert.NotEmpty(sqlMigrations);
            }
            else
            {
                Assert.Empty(sqlMigrations);
            }
        }

        var allBusinessMigrations = Directory.GetFiles(RepositoryRoot, "*.sql", SearchOption.AllDirectories)
            .Where(path => !HasIgnoredSegment(path))
            .Select(Path.GetFullPath)
            .ToArray();
        Assert.All(allBusinessMigrations, path =>
            Assert.Contains(authorizedRoots, root => IsUnderRoot(path, root)));
    }

    [Fact]
    public void PhysicalPersistenceDependenciesRemainInsideGovernedServiceBoundaries()
    {
        var forbidden = new[]
        {
            "EntityFrameworkCore",
            "SqlClient",
            "MongoDB",
            "StackExchange.Redis",
            "Azure.",
            "Google.Cloud",
            "OpenAI",
        };
        var persistenceCohort = new HashSet<string>(StringComparer.Ordinal)
        {
            "audit",
            "evidence",
            "financial-profile",
            "financial-rules",
            "reporting",
            "job-management",
            "customer-identity", // AR-001 / AM-05 owner-local tenant sessions.
        };

        foreach (var projectPath in Directory.GetFiles(RepositoryRoot, "*.csproj", SearchOption.AllDirectories))
        {
            if (HasIgnoredSegment(projectPath)) continue;
            var content = File.ReadAllText(projectPath);
            Assert.DoesNotContain(forbidden, value => content.Contains(value, StringComparison.OrdinalIgnoreCase));

            var relative = Path.GetRelativePath(RepositoryRoot, projectPath).Replace('\\', '/');
            var packages = XDocument.Load(projectPath)
                .Descendants("PackageReference")
                .Select(element => element.Attribute("Include")?.Value)
                .Where(value => value is not null)
                .Cast<string>()
                .ToArray();
            var serviceId = relative.StartsWith("services/", StringComparison.Ordinal)
                ? relative.Split('/')[1]
                : null;
            var persistenceBoundary = serviceId is not null && persistenceCohort.Contains(serviceId)
                || relative.StartsWith("tests/persistence/", StringComparison.Ordinal)
                || relative.StartsWith("tests/job-management/", StringComparison.Ordinal)
                || relative.StartsWith("build/Monergy.DatabaseMigrator/", StringComparison.Ordinal);

            if (packages.Contains("Npgsql", StringComparer.OrdinalIgnoreCase)
                || packages.Contains("Dapper", StringComparer.OrdinalIgnoreCase))
            {
                Assert.True(persistenceBoundary, $"D09 database dependency escaped its boundary: {relative}");
            }
            if (packages.Contains("AWSSDK.S3", StringComparer.OrdinalIgnoreCase))
            {
                Assert.True(relative.StartsWith("services/evidence/", StringComparison.Ordinal)
                    || relative.StartsWith("tests/persistence/", StringComparison.Ordinal),
                    $"S3 dependency escaped Evidence/D09 test scope: {relative}");
            }
            if (packages.Contains("dbup-postgresql", StringComparer.OrdinalIgnoreCase))
            {
                Assert.True(relative.StartsWith("build/Monergy.DatabaseMigrator/", StringComparison.Ordinal),
                    $"DbUp dependency escaped D09 migration tooling: {relative}");
            }
        }

        var persistenceSource = Directory.GetFiles(RepositoryRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !HasIgnoredSegment(path))
            .Where(path =>
            {
                var content = File.ReadAllText(path);
                return System.Text.RegularExpressions.Regex.IsMatch(
                    content,
                    @"(?m)^\s*using\s+(Npgsql|Amazon\.|DbUp)");
            })
            .Select(path => Path.GetRelativePath(RepositoryRoot, path).Replace('\\', '/'))
            .ToArray();
        Assert.All(persistenceSource, relative => Assert.True(
            persistenceCohort.Any(serviceId => relative.StartsWith($"services/{serviceId}/Infrastructure/", StringComparison.Ordinal))
            || relative.StartsWith("tests/persistence/", StringComparison.Ordinal)
            || relative.StartsWith("tests/job-management/", StringComparison.Ordinal)
            || relative.StartsWith("build/Monergy.DatabaseMigrator/", StringComparison.Ordinal),
            $"Physical persistence source escaped its D09 boundary: {relative}"));
    }

    [Fact]
    public void PlatformMetadataCannotBeMistakenForAProductFeature()
    {
        var metadata = ComponentMetadata.Create("test-component");

        Assert.Equal(ComponentMetadata.ToolchainOnlyState, metadata.ImplementationState);
        Assert.Equal("test-component", metadata.Component);
    }

    [Fact]
    public void FinancialProfileApplicationOwnsFinancialProfileChangedSemantics()
    {
        var application = File.ReadAllText(Path.Combine(
            ServicesRoot,
            "financial-profile",
            "Application",
            "FinancialProfileApplication.cs"));
        var infrastructure = File.ReadAllText(Path.Combine(
            ServicesRoot,
            "financial-profile",
            "Infrastructure",
            "InMemoryFinancialProfileRepository.cs"));

        Assert.Contains("FinancialProfileChangeTransition", application, StringComparison.Ordinal);
        Assert.Contains("CID-036", application, StringComparison.Ordinal);
        Assert.Contains("FinancialProfileChangedPayload", application, StringComparison.Ordinal);
        Assert.Contains("profileChange.CreateEvent", infrastructure, StringComparison.Ordinal);
        Assert.DoesNotContain("CID-036", infrastructure, StringComparison.Ordinal);
        Assert.DoesNotContain("FinancialProfileChangedPayload", infrastructure, StringComparison.Ordinal);
    }

    [Fact]
    public void FinancialRulesApplicationOwnsOutcomeSemanticsAndAdaptersHaveNoForeignRepositoryAccess()
    {
        var application = File.ReadAllText(Path.Combine(ServicesRoot, "financial-rules", "Application", "FinancialRulesApplication.cs"));
        var infrastructure = string.Join('\n', Directory.GetFiles(Path.Combine(ServicesRoot, "financial-rules", "Infrastructure"), "*.cs").Select(File.ReadAllText));
        Assert.Contains("CID-040", application, StringComparison.Ordinal);
        Assert.Contains("CID-041", application, StringComparison.Ordinal);
        Assert.DoesNotContain("new DomainEvent", infrastructure, StringComparison.Ordinal);
        Assert.DoesNotContain("FinancialProfileRepository", infrastructure, StringComparison.Ordinal);
        Assert.DoesNotContain("AuditRepository", infrastructure, StringComparison.Ordinal);
        Assert.DoesNotContain("FinancialFactRecord", application + infrastructure, StringComparison.Ordinal);
    }

    [Fact]
    public void SearchRetrievalOwnsOnlyAuthorizedDerivedIndexesAndSourceReferences()
    {
        var application = File.ReadAllText(Path.Combine(ServicesRoot, "search-retrieval", "Application", "SearchRetrievalApplication.cs"));
        var domain = File.ReadAllText(Path.Combine(ServicesRoot, "search-retrieval", "Domain", "SearchDomain.cs"));
        var infrastructure = File.ReadAllText(Path.Combine(ServicesRoot, "search-retrieval", "Infrastructure", "ReferenceSearchAdapters.cs"));
        Assert.Contains("ISearchAuthorizationPolicy", application, StringComparison.Ordinal);
        Assert.Contains("DERIVED_REBUILDABLE", domain, StringComparison.Ordinal);
        Assert.Contains("SearchSourceReference", domain, StringComparison.Ordinal);
        Assert.Contains("RequiredAuthorizationContextId", infrastructure, StringComparison.Ordinal);
        Assert.DoesNotContain("FinancialProfileRepository", application + infrastructure, StringComparison.Ordinal);
        Assert.DoesNotContain("EvidenceRepository", application + infrastructure, StringComparison.Ordinal);
    }

    [Fact]
    public void ReportingOwnsReportLifecycleButRetainsExternalAuthorityReferences()
    {
        var application = File.ReadAllText(Path.Combine(ServicesRoot, "reporting", "Application", "ReportingApplication.cs"));
        var domain = File.ReadAllText(Path.Combine(ServicesRoot, "reporting", "Domain", "ReportingDomain.cs"));
        var infrastructure = File.ReadAllText(Path.Combine(ServicesRoot, "reporting", "Infrastructure", "ReferenceReportingAdapters.cs"));
        var contracts = File.ReadAllText(Path.Combine(RepositoryRoot, "contracts", "Monergy.Contracts", "D08ReportingContracts.cs"));
        Assert.Contains("ReportSourceReference", contracts, StringComparison.Ordinal);
        Assert.Contains("Financial Profile Service", domain, StringComparison.Ordinal);
        Assert.Contains("Financial Rules Service", domain, StringComparison.Ordinal);
        Assert.Contains("AuditCompatibilityReferenceId", application, StringComparison.Ordinal);
        Assert.DoesNotContain("FinancialProfileRepository", application + infrastructure, StringComparison.Ordinal);
        Assert.DoesNotContain("FinancialRulesRepository", application + infrastructure, StringComparison.Ordinal);
        Assert.DoesNotContain("EvidenceRepository", application + infrastructure, StringComparison.Ordinal);
    }

    private static XDocument LoadServiceProject(string serviceId) =>
        XDocument.Load(Directory.GetFiles(Path.Combine(ServicesRoot, serviceId), "*.csproj").Single());

    private static string ProjectServiceId(string path) =>
        new DirectoryInfo(Path.GetDirectoryName(path)!).Name;

    private static bool HasIgnoredSegment(string path)
    {
        var relative = Path.GetRelativePath(RepositoryRoot, path).Replace('\\', '/');
        return relative.Split('/').Any(segment => segment is ".git" or ".artifacts" or ".toolcache" or "bin" or "node_modules" or "obj");
    }

    private static bool IsUnderRoot(string path, string root) =>
        path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

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

