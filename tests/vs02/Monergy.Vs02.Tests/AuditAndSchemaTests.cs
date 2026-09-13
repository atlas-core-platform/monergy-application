using System.Text.Json;
using Monergy.Contracts;
using Monergy.Services.Audit.Application;
using Monergy.Services.Audit.Infrastructure;
using Xunit;

namespace Monergy.Vs02.Tests;

public sealed class AuditAndSchemaTests
{
    [Fact]
    public async Task AuditEvidenceIsAppendOnlyAndDuplicateDeliveryIsSafe()
    {
        var app = new AuditApplication(new AppendOnlyInMemoryAuditRepository(), new CapturingTelemetry(), TimeProvider.System);
        var source = new AuditableEvent(
            "CID-023",
            "event-001",
            Vs02ContractNames.EvidenceRegistered,
            ContractGuard.CurrentVersion,
            DateTimeOffset.UtcNow,
            Vs02TestContext.CorrelationId,
            "request-001",
            "Evidence Service",
            "evidence",
            "evidence-001");

        var first = await app.ConsumeAsync(source);
        var replay = await app.ConsumeAsync(source);

        Assert.Equal(first, replay);
        Assert.Single(await app.ReadAllAsync());
    }

    [Fact]
    public void MachineReadableSchemaContainsOnlyTheExactVs02ContractSet()
    {
        var root = FindRepositoryRoot();
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "contracts", "schemas", "vs02-contracts.schema.json")));
        var ids = document.RootElement
            .GetProperty("properties")
            .GetProperty("contractId")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToArray();

        Assert.Equal(Vs02ContractCatalog.All.Select(contract => contract.Id), ids);
        Assert.Equal("1.0.0", document.RootElement.GetProperty("properties").GetProperty("contractVersion").GetProperty("const").GetString());
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "repository.manifest.json")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
