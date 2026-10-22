using Microsoft.Extensions.Configuration;
using Monergy.Platform;
using Monergy.Services.DocumentIntelligence.Application;
using Monergy.Services.DocumentIntelligence.Infrastructure;
using Npgsql;
using Xunit;
namespace Monergy.Vs02.Tests;
public sealed class ClassificationReviewPhysicalTests
{
    [Fact, Trait("Category", "Physical")]
    public async Task PhysicalReviewsSurviveAdapterRestartAndRejectStaleWriters()
    {
        var connection = Environment.GetEnvironmentVariable("T111_CLASSIFICATION_RUNTIME_CONNECTION") ?? throw new InvalidOperationException("Supply a migrated isolated test database runtime connection.");
        var config = new ConfigurationManager { ["Monergy:ExecutionZone"] = "CI_EPHEMERAL", ["Monergy:Classification:RuntimeConnections:T001"] = connection };
        var suffix = Guid.NewGuid().ToString("N"); var row = new DocumentClassification("T001", "customer-" + suffix, "version-" + suffix, new('a', 64), ClassificationRules.Version, "UNCLASSIFIED", "NO_TEXT", DateTimeOffset.UtcNow, 1, []);
        await using (var first = new PostgresDocumentClassificationRepository(config))
        {
            await first.RecordAsync(row, default);
            await first.ReviewAsync(row.TenantId, row.CustomerId, row.DocumentVersionId, "A900", new(1, "OTHER", "OTHER_DOCUMENT"), DateTimeOffset.UtcNow, default);
        }
        await using (var restarted = new PostgresDocumentClassificationRepository(config))
        {
            var retained = await restarted.ReadAsync(row.TenantId, row.CustomerId, row.DocumentVersionId, default);
            Assert.Equal(2, retained!.Revision); Assert.Single(retained.Reviews); Assert.Equal("OTHER", retained.DocumentClass);
            Assert.Equal(retained.SourceHash, (await restarted.RecordAsync(row, default)).SourceHash);
            var error = await Assert.ThrowsAsync<TenantBoundaryException>(() => restarted.ReviewAsync(row.TenantId, row.CustomerId, row.DocumentVersionId, "A901", new(1, "PAN", "SOURCE_REVIEWED"), DateTimeOffset.UtcNow, default)); Assert.Equal(409, error.Status);
        }
        await using var db = new NpgsqlConnection(connection); await db.OpenAsync();
        await using var grants = new NpgsqlCommand("SELECT has_table_privilege(current_user,'document_classifications','DELETE,TRUNCATE')", db);
        Assert.False((bool)(await grants.ExecuteScalarAsync())!);
    }
}
