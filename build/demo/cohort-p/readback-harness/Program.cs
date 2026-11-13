using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Monergy.Services.Evidence.Infrastructure;
using Npgsql;

if (args.Length != 5) throw new InvalidOperationException("Expected configuration, exact-version, provider, classification and output paths.");
using var source = JsonDocument.Parse(await File.ReadAllTextAsync(args[0]));
using var exact = JsonDocument.Parse(await File.ReadAllTextAsync(args[1]));
using var provider = JsonDocument.Parse(await File.ReadAllTextAsync(args[2]));
using var classification = JsonDocument.Parse(await File.ReadAllTextAsync(args[3]));
var root = source.RootElement;
var s3 = root.GetProperty("S3");
var database = new NpgsqlConnectionStringBuilder(root.GetProperty("RuntimeConnections").GetProperty("T001").GetString())
    { Host = "127.0.0.1" };
var service = new UriBuilder(s3.GetProperty("Endpoint").GetString()!) { Host = "127.0.0.1" };
var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
{
    ["Monergy:ExecutionZone"] = "LOCAL",
    ["Monergy:Persistence:Provider"] = "POSTGRESQL_S3",
    ["Monergy:Persistence:Evidence:RuntimeConnection"] = database.ConnectionString,
    ["Monergy:Persistence:Evidence:S3:Bucket"] = s3.GetProperty("Bucket").GetString(),
    ["Monergy:Persistence:Evidence:S3:Endpoint"] = service.Uri.ToString().TrimEnd('/'),
    ["Monergy:Persistence:Evidence:S3:AccessKey"] = s3.GetProperty("AccessKey").GetString(),
    ["Monergy:Persistence:Evidence:S3:SecretKey"] = s3.GetProperty("SecretKey").GetString(),
}).Build();
await using var repository = new PostgresEvidenceRepository(configuration);
using var store = new S3EvidenceContentStore(configuration);

var rows = new List<object>();
await ReadAsync(exact.RootElement.GetProperty("DocumentVersionId").GetString()!,
    exact.RootElement.GetProperty("originalSha256").GetString()!,
    exact.RootElement.GetProperty("registeredObjectVersion").GetString()!, "exact-version-probe");
foreach (var name in new[] { "GoogleDrive", "OneDrive" })
{
    var item = provider.RootElement.GetProperty(name);
    await ReadAsync(item.GetProperty("DocumentVersionId").GetString()!, item.GetProperty("Sha256").GetString()!, null, name);
}
var review = classification.RootElement.GetProperty("review");
var result = new
{
    bucket = s3.GetProperty("Bucket").GetString(),
    evidence = rows,
    classificationReview = new { documentVersionId = review.GetProperty("documentVersionId").GetString(),
        revision = review.GetProperty("afterRevision").GetInt64(), historyCount = review.GetProperty("historyCount").GetInt32() },
};
var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
await File.WriteAllTextAsync(args[4], json);
Console.WriteLine(json);

async Task ReadAsync(string versionId, string expectedHash, string? expectedStorageVersion, string label)
{
    var record = await repository.GetVersionAsync(versionId, default)
        ?? throw new InvalidOperationException(label + " Evidence row is unavailable.");
    var bytes = await store.ReadRegisteredAsync(record, default)
        ?? throw new InvalidOperationException(label + " original is unavailable.");
    var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
    if (hash != expectedHash || hash != record.ContentSha256 || bytes.LongLength != record.ContentLength ||
        expectedStorageVersion is not null && record.ContentStorageVersionId != expectedStorageVersion)
        throw new InvalidOperationException(label + " exact readback changed.");
    rows.Add(new { label, record.TenantId, record.CustomerId, record.DocumentId, record.DocumentVersionId, record.EvidenceId,
        record.ContentStorageKey, record.ContentStorageVersionId, record.ContentLength, record.ContentType, sha256 = hash,
        source = record.SourceLocation });
}
