using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Configuration;
using Monergy.Contracts;
using Monergy.Services.Evidence.Application;
using Monergy.Services.Evidence.Infrastructure;
using Npgsql;

if (args.Length != 3) throw new InvalidOperationException("Expected Evidence configuration, probe input and output paths.");
using var source = JsonDocument.Parse(await File.ReadAllTextAsync(args[0]));
using var input = JsonDocument.Parse(await File.ReadAllTextAsync(args[1]));
var root = source.RootElement;
var s3 = root.GetProperty("S3");
var database = new NpgsqlConnectionStringBuilder(root.GetProperty("RuntimeConnections").GetProperty("T001").GetString())
    { Host = "127.0.0.1" };
var service = new UriBuilder(s3.GetProperty("Endpoint").GetString()!) { Host = "127.0.0.1" };
var values = new Dictionary<string, string?>
{
    ["Monergy:ExecutionZone"] = "LOCAL",
    ["Monergy:Persistence:Provider"] = "POSTGRESQL_S3",
    ["Monergy:Persistence:Evidence:RuntimeConnection"] = database.ConnectionString,
    ["Monergy:Persistence:Evidence:S3:Bucket"] = s3.GetProperty("Bucket").GetString(),
    ["Monergy:Persistence:Evidence:S3:Endpoint"] = service.Uri.ToString().TrimEnd('/'),
    ["Monergy:Persistence:Evidence:S3:AccessKey"] = s3.GetProperty("AccessKey").GetString(),
    ["Monergy:Persistence:Evidence:S3:SecretKey"] = s3.GetProperty("SecretKey").GetString(),
};
var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
var bucket = values["Monergy:Persistence:Evidence:S3:Bucket"]!;
var endpoint = values["Monergy:Persistence:Evidence:S3:Endpoint"]!;
using var client = new AmazonS3Client(new BasicAWSCredentials(
    values["Monergy:Persistence:Evidence:S3:AccessKey"], values["Monergy:Persistence:Evidence:S3:SecretKey"]),
    new AmazonS3Config { ServiceURL = endpoint, ForcePathStyle = true, UseHttp = new Uri(endpoint).Scheme == Uri.UriSchemeHttp });
await using var repository = new PostgresEvidenceRepository(configuration);
using var store = new S3EvidenceContentStore(configuration);

var versionId = input.RootElement.GetProperty("documentVersionId").GetString()!;
var version = await repository.GetVersionAsync(versionId, default)
    ?? throw new InvalidOperationException("Physical Evidence row was not found.");
var storageKey = version.ContentStorageKey ?? throw new InvalidOperationException("Evidence storage key is unavailable.");
var registeredStorageVersion = version.ContentStorageVersionId ?? throw new InvalidOperationException("Evidence storage version is unavailable.");
var versioning = await client.GetBucketVersioningAsync(new GetBucketVersioningRequest { BucketName = bucket });
if (versioning.VersioningConfig?.Status != VersionStatus.Enabled) throw new InvalidOperationException("Bucket versioning is not enabled.");

var original = await store.ReadRegisteredAsync(version, default)
    ?? throw new InvalidOperationException("Registered original is unavailable.");
var originalHash = Convert.ToHexStringLower(SHA256.HashData(original));
if (originalHash != version.ContentSha256 || original.LongLength != version.ContentLength)
    throw new InvalidOperationException("Registered original bytes do not match Evidence.");

var receiptKey = storageKey + ".scan.json";
EvidenceContentDescriptor receipt;
string receiptVersionBefore;
using (var response = await client.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = receiptKey }))
{
    receiptVersionBefore = response.VersionId;
    receipt = await JsonSerializer.DeserializeAsync<EvidenceContentDescriptor>(response.ResponseStream, ContractJson.Options)
        ?? throw new InvalidOperationException("Scan receipt is invalid.");
}
if (receipt.StorageVersionId != registeredStorageVersion || receipt.Sha256 != version.ContentSha256 || receipt.MalwareCheck is null)
    throw new InvalidOperationException("Scan receipt does not bind the registered original.");

var originalVersionsBefore = await VersionsAsync(client, bucket, storageKey);
await client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = receiptKey });
var operationId = version.ContentReference["s3-intake://".Length..];
var recovered = await store.StageScannedAsync(operationId, original, version.ContentType, receipt.MalwareCheck, version.SourceLocation, default);
var originalVersionsAfter = await VersionsAsync(client, bucket, storageKey);
if (recovered.StorageVersionId != registeredStorageVersion || recovered.Sha256 != version.ContentSha256 ||
    originalVersionsBefore.Count != originalVersionsAfter.Count)
    throw new InvalidOperationException("Interrupted receipt recovery replaced the durable original.");
string receiptVersionAfter;
using (var response = await client.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = receiptKey }))
    receiptVersionAfter = response.VersionId;
if (receiptVersionAfter == receiptVersionBefore) throw new InvalidOperationException("Scan receipt recovery did not persist a new receipt version.");

var newerBytes = Encoding.UTF8.GetBytes("FORM NO. 16\nCohort P deliberately newer storage object.\n");
var newerHash = Convert.ToHexStringLower(SHA256.HashData(newerBytes));
using var newerStream = new MemoryStream(newerBytes, false);
var newer = await client.PutObjectAsync(new PutObjectRequest
{
    BucketName = bucket,
    Key = storageKey,
    InputStream = newerStream,
    ContentType = version.ContentType,
    Metadata = { ["monergy-sha256"] = newerHash, ["monergy-reference"] = version.ContentReference },
});
if (string.IsNullOrWhiteSpace(newer.VersionId) || newer.VersionId == registeredStorageVersion)
    throw new InvalidOperationException("A distinct newer object version was not created.");

var exactAfterNewer = await store.ReadRegisteredAsync(version, default)
    ?? throw new InvalidOperationException("Exact registered version disappeared after a newer object was created.");
if (!exactAfterNewer.AsSpan().SequenceEqual(original))
    throw new InvalidOperationException("Registered read substituted the newest object.");
using var latest = await client.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = storageKey });
if (latest.VersionId != newer.VersionId) throw new InvalidOperationException("Newer object was not the current storage version.");

var missingVersion = await FailsClosedAsync(() => store.ReadRegisteredAsync(version with { ContentStorageVersionId = new string('f', 32) }, default));
var wrongHash = await FailsClosedAsync(() => store.ReadRegisteredAsync(version with { ContentSha256 = new string('0', 64) }, default));
var wrongLength = await FailsClosedAsync(() => store.ReadRegisteredAsync(version with { ContentLength = version.ContentLength + 1 }, default));
var malformedKey = await FailsClosedAsync(() => store.ReadRegisteredAsync(version with { ContentStorageKey = "evidence/untrusted" }, default));
if (!(missingVersion && wrongHash && wrongLength && malformedKey)) throw new InvalidOperationException("A storage corruption probe did not fail closed.");

var result = new
{
    bucket,
    versioning = "Enabled",
    version.TenantId,
    version.CustomerId,
    version.DocumentId,
    version.DocumentVersionId,
    version.EvidenceId,
    version.ContentReference,
    ContentStorageKey = storageKey,
    registeredObjectVersion = registeredStorageVersion,
    originalSha256 = originalHash,
    version.ContentLength,
    version.ContentType,
    receiptRecovery = new { originalObjectVersionsBefore = originalVersionsBefore.Count, originalObjectVersionsAfter = originalVersionsAfter.Count,
        receiptVersionChanged = receiptVersionBefore != receiptVersionAfter, sameRegisteredObjectVersion = recovered.StorageVersionId == registeredStorageVersion },
    newerObject = new { versionId = newer.VersionId, sha256 = newerHash, registeredReadRemainedExact = true },
    failClosed = new { missingVersion, wrongHash, wrongLength, malformedKey },
};
var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
await File.WriteAllTextAsync(args[2], json);
Console.WriteLine(json);

static async Task<List<string>> VersionsAsync(IAmazonS3 client, string bucket, string key)
{
    var response = await client.ListVersionsAsync(new ListVersionsRequest { BucketName = bucket, Prefix = key });
    return response.Versions.Where(item => item.Key == key).Select(item => item.VersionId).ToList();
}

static async Task<bool> FailsClosedAsync(Func<Task<byte[]?>> action)
{
    try { return await action() is null; }
    catch (EvidenceStorageUnavailableException) { return true; }
}
