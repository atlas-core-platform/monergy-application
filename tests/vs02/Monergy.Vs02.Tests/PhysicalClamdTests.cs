using System.Globalization;
using Microsoft.Extensions.Configuration;
using Monergy.Services.Evidence.Application;
using Monergy.Services.Evidence.Infrastructure;
using Xunit;

namespace Monergy.Vs02.Tests;

[Trait("Category", "Physical")]
public sealed class PhysicalClamdTests
{
    [Fact]
    public async Task LiveClamdAcceptsBenignBytesRejectsTheHarmlessEicarTestAndReportsFreshMetadata()
    {
        var port = Environment.GetEnvironmentVariable("MONERGY_CLAMD_TEST_PORT")
            ?? throw new InvalidOperationException("A live isolated ClamD fixture is required.");
        _ = int.Parse(port, CultureInfo.InvariantCulture);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["Monergy:ReferenceAdapters"] = "true", ["Monergy:ExecutionZone"] = "CI_EPHEMERAL", ["Monergy:ProductIntake:ClamdPort"] = port }).Build();
        using var scanner = new ClamdEvidenceMalwareScanner(configuration, TimeProvider.System);
        Assert.True(await scanner.ReadyAsync(default));
        var clean = await scanner.CheckAsync("Monergy harmless scanner acceptance fixture."u8.ToArray(), default);
        Assert.True(EvidenceMalwarePolicy.Verified(clean, clean.ContentSha256, DateTimeOffset.UtcNow));
        // Standard harmless antivirus test, assembled only at test execution.
        var eicar = System.Text.Encoding.ASCII.GetBytes(string.Concat("X5O!P%@AP[4", "\\PZX54(P^)7CC)7}$", "EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*"));
        await Assert.ThrowsAsync<EvidenceScanRejectedException>(() => scanner.CheckAsync(eicar, default));
    }
}
