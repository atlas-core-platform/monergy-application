using Microsoft.Extensions.Configuration;
using Monergy.Services.Evidence.Application;
using Monergy.Services.Evidence.Infrastructure;
using Xunit;

namespace Monergy.Vs02.Tests;

public sealed class DocumentVaultPhysicalTests
{
    [Fact, Trait("Category", "Physical")]
    public async Task VaultMetadataProviderLocationReplayAndReplacementSurviveRestart()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["Monergy:ExecutionZone"] = "CI_EPHEMERAL", ["Monergy:Persistence:Provider"] = "POSTGRESQL_S3",
            ["Monergy:Persistence:Evidence:RuntimeConnection"] = Environment.GetEnvironmentVariable("T193_EVIDENCE_RUNTIME_CONNECTION") }).Build();
        var id = Guid.NewGuid().ToString("N"); var scope = new VaultScope("T001", "customer-" + id); var now = DateTimeOffset.UtcNow;
        var original = new EvidenceVersionRecord("document-" + id, "version-" + id, "evidence-" + id, scope.CustomerId, 0, "Google Drive", "statement.txt", "text/plain", "reference://physical-fixture", new('a', 64), now, now,
            TenantId: scope.TenantId, SourceLocation: new("google-drive", "folder", "file", "revision"));
        var change = new VaultMetadataChange("metadata-" + id, 0, "Banking", "Bank statement", "2026-04-01", "2026-2027");
        VaultMetadata receipt;
        await using (var repo = new PostgresEvidenceRepository(cfg))
        {
            await repo.SaveVersionAsync(new("CreateDocumentVersion", "1.0.0", scope.CustomerId, id), original, _ => [], default);
            receipt = await repo.VaultSaveAsync(scope, original.DocumentVersionId, "A100", change, default);
        }
        await using var restarted = new PostgresEvidenceRepository(cfg);
        var row = Assert.Single((await restarted.VaultListAsync(scope, "Banking", "statement", null, default)).Items);
        Assert.Equal(original.SourceLocation, row.SourceLocation); Assert.Equal(receipt, row.Metadata);
        Assert.Equal(receipt, await restarted.VaultSaveAsync(scope, original.DocumentVersionId, "A100", change, default));
        var next = original with { DocumentVersionId = "next-" + id, EvidenceId = "next-evidence-" + id, ContentSha256 = new('b', 64), ExpectedPreviousVersionId = original.DocumentVersionId };
        var op = new EvidenceOperationIdentity("CreateDocumentVersion", "1.0.0", scope.CustomerId, "next-" + id);
        Assert.True((await restarted.SaveVersionAsync(op, next, _ => [], default)).Created);
        Assert.False((await restarted.SaveVersionAsync(op, next, _ => [], default)).Created);
        Assert.Equal(2, (await restarted.GetDocumentAsync(original.DocumentId, default)).Count);
        Assert.Null(await restarted.VaultReadAsync(scope with { TenantId = "T002" }, original.DocumentVersionId, default));
    }
}
