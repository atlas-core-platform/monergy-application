using System.Text.Json;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Configuration;
using Monergy.Services.Evidence.Infrastructure;
using Npgsql;

if (args.Length != 2) throw new InvalidOperationException("Expected protected Evidence configuration and output paths.");
using var source = JsonDocument.Parse(await File.ReadAllTextAsync(args[0]));
var root = source.RootElement;
var s3 = root.GetProperty("S3");
var database = new NpgsqlConnectionStringBuilder(root.GetProperty("RuntimeConnections").GetProperty("T001").GetString())
    { Host = "127.0.0.1" };
var service = new UriBuilder(s3.GetProperty("Endpoint").GetString()!) { Host = "127.0.0.1" };
var bucket = s3.GetProperty("Bucket").GetString()!;
var access = s3.GetProperty("AccessKey").GetString()!;
var secret = s3.GetProperty("SecretKey").GetString()!;
using var client = Client(service.Uri.ToString().TrimEnd('/'), access, secret);

var negativeBucket = bucket + "-negative-versioning";
var buckets = await client.ListBucketsAsync();
if (buckets.Buckets?.Any(item => item.BucketName == negativeBucket) == true)
    await client.DeleteBucketAsync(new DeleteBucketRequest { BucketName = negativeBucket });
await client.PutBucketAsync(new PutBucketRequest { BucketName = negativeBucket });
var disabledVersioning = false;
try
{
    var store = new S3EvidenceContentStore(client, negativeBucket);
    disabledVersioning = await FailsAsync(() => store.CheckReadyAsync(default));
}
finally
{
    await client.DeleteBucketAsync(new DeleteBucketRequest { BucketName = negativeBucket });
}

using var unavailableClient = Client("http://127.0.0.1:1", access, secret);
using var unavailableStore = new S3EvidenceContentStore(unavailableClient, bucket);
var unavailableStorage = await FailsAsync(() => unavailableStore.CheckReadyAsync(default));

var missingDatabase = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Database = "monergy_evidence_missing" };
var unavailableConfig = Configuration(new Dictionary<string, string?>
{
    ["Monergy:Persistence:Evidence:RuntimeConnection"] = missingDatabase.ConnectionString,
});
await using var unavailableRepository = new PostgresEvidenceRepository(unavailableConfig);
var unavailableSchema = await FailsAsync(() => unavailableRepository.CheckReadyAsync(default));

var malformedConfiguration = false;
try { _ = new S3EvidenceContentStore(Configuration([])); }
catch (InvalidOperationException) { malformedConfiguration = true; }

if (!(disabledVersioning && unavailableStorage && unavailableSchema && malformedConfiguration))
    throw new InvalidOperationException("A negative readiness contract did not fail closed.");
var result = new { disabledVersioning, unavailableStorage, unavailableSchema, malformedConfiguration, volatileFallback = false };
var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
await File.WriteAllTextAsync(args[1], json);
Console.WriteLine(json);

static AmazonS3Client Client(string endpoint, string access, string secret) => new(new BasicAWSCredentials(access, secret),
    new AmazonS3Config { ServiceURL = endpoint, ForcePathStyle = true, UseHttp = new Uri(endpoint).Scheme == Uri.UriSchemeHttp,
        Timeout = TimeSpan.FromSeconds(2), MaxErrorRetry = 0 });

static IConfiguration Configuration(IEnumerable<KeyValuePair<string, string?>> extra)
{
    var values = new Dictionary<string, string?>
    {
        ["Monergy:ExecutionZone"] = "LOCAL",
        ["Monergy:Persistence:Provider"] = "POSTGRESQL_S3",
    };
    foreach (var item in extra) values[item.Key] = item.Value;
    return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}

static async Task<bool> FailsAsync(Func<Task> action)
{
    try { await action(); return false; }
    catch { return true; }
}
