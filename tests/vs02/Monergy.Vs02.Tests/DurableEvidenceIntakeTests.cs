using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.Evidence;
using Monergy.Services.Evidence.Application;
using Monergy.Services.Evidence.Infrastructure;
using Xunit;

namespace Monergy.Vs02.Tests;

// Deterministic S3 protocol double; these checks do not claim a live object-store run.
public sealed class DurableEvidenceIntakeTests
{
    private const string Operation = "cccccccccccccccccccccccccccccccc";
    [Fact]
    public async Task RestartRetainsScannedOriginalAndExactVersionDespiteNewerObject()
    {
        var s3 = new MemoryS3(); var store = new S3EvidenceContentStore(s3, "evidence");
        var bytes = "original source"u8.ToArray();
        var source = new EvidenceSourceLocation("google-drive", new string('f', 512), new string('i', 512), new string('v', 512));
        var first = await store.StageScannedAsync(Operation, bytes, "text/plain", FixtureMalwareScanner.Check(bytes), source, default);
        var restarted = new S3EvidenceContentStore(s3, "evidence");
        Assert.Equal(first, await restarted.GetDescriptorAsync(first.Reference, default));
        var replay = await restarted.StageScannedAsync(Operation, bytes, "text/plain", FixtureMalwareScanner.Check(bytes), source, default);
        Assert.Equal(first.StorageVersionId, replay.StorageVersionId);
        Assert.Equal(1, s3.OriginalWrites);
        var version = Version(first);
        s3.ReplaceLatest(first.StorageKey!, "new unrelated bytes"u8.ToArray());
        Assert.Equal(bytes, await restarted.ReadRegisteredAsync(version, default));
        Assert.Equal(first.StorageVersionId, s3.LastReadVersion);
        Assert.Equal(source, replay.SourceLocation);
    }
    [Theory]
    [InlineData("bytes")]
    [InlineData("mime")]
    [InlineData("source")]
    public async Task OperationCannotBeReboundToDifferentContentOrSource(string mode)
    {
        var s3 = new MemoryS3(); var store = new S3EvidenceContentStore(s3, "evidence"); var bytes = "source"u8.ToArray();
        await store.StageScannedAsync(Operation, bytes, "text/plain", FixtureMalwareScanner.Check(bytes), null, default);
        var next = mode == "bytes" ? "different"u8.ToArray() : bytes;
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.StageScannedAsync(Operation, next,
            mode == "mime" ? "text/csv" : "text/plain", FixtureMalwareScanner.Check(next),
            mode == "source" ? new("onedrive", "folder", "file", "version") : null, default));
        Assert.Equal(1, s3.OriginalWrites);
    }
    [Fact]
    public async Task LostReceiptWriteCanBeRecoveredWithoutRewritingOriginal()
    {
        var s3 = new MemoryS3 { FailReceipt = true }; var store = new S3EvidenceContentStore(s3, "evidence"); var bytes = "source"u8.ToArray();
        await Assert.ThrowsAsync<EvidenceStorageUnavailableException>(() => store.StageScannedAsync(Operation, bytes, "text/plain", FixtureMalwareScanner.Check(bytes), null, default));
        Assert.Null(await store.GetDescriptorAsync(EvidenceIntakeReference.DurablePrefix + Operation, default));
        s3.FailReceipt = false;
        var result = await new S3EvidenceContentStore(s3, "evidence").StageScannedAsync(Operation, bytes, "text/plain", FixtureMalwareScanner.Check(bytes), null, default);
        Assert.NotNull(result.MalwareCheck); Assert.Equal(1, s3.OriginalWrites);
    }
    [Fact]
    public async Task InvalidScanAndDisabledVersioningNeverStoreOriginals()
    {
        var s3 = new MemoryS3(); var store = new S3EvidenceContentStore(s3, "evidence"); var bytes = "source"u8.ToArray();
        await Assert.ThrowsAsync<EvidenceScanUnavailableException>(() => store.StageScannedAsync(Operation, bytes, "text/plain",
            FixtureMalwareScanner.Check(bytes) with { ContentSha256 = new string('a', 64) }, null, default));
        s3.Versioned = false;
        await Assert.ThrowsAsync<EvidenceStorageUnavailableException>(() => store.StageScannedAsync(Operation, bytes, "text/plain", FixtureMalwareScanner.Check(bytes), null, default));
        Assert.Equal(0, s3.OriginalWrites);
    }
    [Theory]
    [InlineData("bytes")]
    [InlineData("missing")]
    [InlineData("key")]
    [InlineData("version")]
    public async Task RegisteredReadNeverFallsBackToDifferentOrCorruptOriginal(string mode)
    {
        var s3 = new MemoryS3(); var store = new S3EvidenceContentStore(s3, "evidence"); var bytes = "source"u8.ToArray();
        var descriptor = await store.StageScannedAsync(Operation, bytes, "text/plain", FixtureMalwareScanner.Check(bytes), null, default);
        var version = Version(descriptor);
        if (mode == "bytes") s3.Corrupt(descriptor.StorageKey!, descriptor.StorageVersionId!);
        if (mode == "missing") { s3.Remove(descriptor.StorageKey!, descriptor.StorageVersionId!); Assert.Null(await store.ReadRegisteredAsync(version, default)); return; }
        if (mode == "key") version = version with { ContentStorageKey = "unrelated-key" };
        if (mode == "version") s3.WrongVersionResponse = true;
        await Assert.ThrowsAsync<EvidenceStorageUnavailableException>(() => store.ReadRegisteredAsync(version, default));
    }
    [Fact]
    public async Task ConditionalReceiptConflictRetriesOnlySameOriginal()
    {
        var s3 = new MemoryS3 { ConflictReceiptOnce = true }; var bytes = "source"u8.ToArray();
        var result = await new S3EvidenceContentStore(s3, "evidence").StageScannedAsync(Operation, bytes, "text/plain", FixtureMalwareScanner.Check(bytes), null, default);
        Assert.NotNull(result.MalwareCheck); Assert.Equal(1, s3.OriginalWrites); Assert.Equal(2, s3.ReceiptAttempts);
    }
    [Fact]
    public async Task ActualPhysicalIntakeEndpointRegistersThenReadsWithExactOwnerBoundary()
    {
        var builder = WebApplication.CreateSlimBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
            ["Monergy:Persistence:Provider"] = "POSTGRESQL_S3", ["Monergy:ExecutionZone"] = "LOCAL",
            ["Monergy:ProductIntake:EvidenceToken"] = new string('a', 64), ["Monergy:ProductIntake:ProcessingToken"] = new string('b', 64),
            ["Monergy:TenantBoundary:CustomerTenants:customer-001"] = "T001" });
        var store = new S3EvidenceContentStore(new MemoryS3(), "evidence"); var repo = new InMemoryEvidenceRepository();
        builder.Services.AddSingleton<IEvidenceIntakeStore>(store); builder.Services.AddSingleton<IEvidenceRepository>(repo);
        builder.Services.AddSingleton<IEvidenceMalwareScanner, FixtureMalwareScanner>();
        await using var app = builder.Build(); app.MapEvidenceReferenceIntake(builder.Configuration); await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        client.DefaultRequestHeaders.Add("X-Monergy-Product-Token", new string('a', 64));
        var bytes = "source"u8.ToArray();
        using var staged = await client.PostAsJsonAsync("/internal/local/v1/evidence/content", new ReferenceEvidenceContentRequest(Operation, Convert.ToBase64String(bytes), "text/plain"));
        Assert.Equal(HttpStatusCode.OK, staged.StatusCode);
        var descriptor = (await staged.Content.ReadFromJsonAsync<EvidenceContentDescriptor>())!;
        var owner = new EvidenceApplication(repo, store, new CapturingTelemetry(), TimeProvider.System);
        var result = await owner.CreateDocumentVersionAsync(Vs02TestContext.Request(Vs02ContractNames.CreateDocumentVersion,
            Vs02TestContext.CreateVersionPayload() with { ContentReference = descriptor.Reference, ContentSha256 = descriptor.Sha256 }, "register", "register",
            Vs02TestContext.Security() with { Workload = new("workspace", "local-uat-workspace:T001") }));
        Assert.Equal(ContractOutcome.Success, result.Outcome);
        client.DefaultRequestHeaders.Add("X-Monergy-Processing-Token", new string('b', 64)); client.DefaultRequestHeaders.Add("X-Monergy-Tenant", "T002");
        using var denied = await client.PostAsJsonAsync("/internal/local/v1/evidence/processing-source", new ReferenceEvidenceReadRequest("document-version-001", "customer-001"));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        client.DefaultRequestHeaders.Remove("X-Monergy-Tenant"); client.DefaultRequestHeaders.Add("X-Monergy-Tenant", "T001");
        using var read = await client.PostAsJsonAsync("/internal/local/v1/evidence/processing-source", new ReferenceEvidenceReadRequest("document-version-001", "customer-001"));
        Assert.Equal(HttpStatusCode.OK, read.StatusCode); Assert.Equal("source", (await read.Content.ReadFromJsonAsync<ReferenceEvidenceReadResult>())!.Content);
        await app.StopAsync();
    }
    [Fact]
    public async Task TenantRoutingRejectsUnknownTenantAndSharedDatabaseBindingsBeforeIo()
    {
        IConfiguration Config(string second) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["Monergy:ExecutionZone"] = "LOCAL", ["Monergy:Persistence:Evidence:RuntimeConnections:T001"] = "Host=127.0.0.1;Database=tenant1;Username=runtime",
            ["Monergy:Persistence:Evidence:RuntimeConnections:T002"] = second }).Build();
        var context = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        Assert.Throws<InvalidOperationException>(() => new TenantEvidenceRepository(Config("Host=127.0.0.1;Database=tenant1;Username=other"), context));
        await using var repo = new TenantEvidenceRepository(Config("Host=127.0.0.1;Database=tenant2;Username=runtime"), context);
        Assert.True(repo.RequireScannedUploads);
        await Assert.ThrowsAsync<TenantBoundaryException>(() => repo.GetVersionAsync("version", default));
        context.HttpContext.Request.Headers["X-Monergy-Tenant"] = "T003";
        await Assert.ThrowsAsync<TenantBoundaryException>(() => repo.GetVersionAsync("version", default));
        context.HttpContext.Request.Headers["X-Monergy-Tenant"] = "T001";
        await Assert.ThrowsAsync<TenantBoundaryException>(() => repo.VaultReadAsync(new("T002", "customer"), "version", default));
    }
    [Fact]
    public void PhysicalProductRegistrationRequiresTenantAuthorizationBoundary()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Monergy:ExecutionZone"] = "LOCAL", ["Monergy:ProductIntake:Enabled"] = "true" }).Build();
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddEvidencePhysicalPersistence(config));
    }

    [Theory]
    [InlineData("reference://customer-intake/cccccccccccccccccccccccccccccccc", true)]
    [InlineData("s3-intake://cccccccccccccccccccccccccccccccc", true)]
    [InlineData("s3-intake://dddddddddddddddddddddddddddddddd", false)]
    [InlineData("reference://customer-intake/cccccccccccccccccccccccccccccccc/suffix", false)]
    [InlineData("https://untrusted/content", false)]
    public void CallersAcceptOnlyTheExactStagingOperation(string reference, bool expected) =>
        Assert.Equal(expected, EvidenceIntakeReference.Matches(reference, Operation));

    private static EvidenceVersionRecord Version(EvidenceContentDescriptor d) => new("document", "version", "evidence", "customer", 1,
        "Upload", "source.txt", d.ContentType, d.Reference, d.Sha256, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
        d.ContentLength, d.StorageKey, d.StorageVersionId, "T001", d.MalwareCheck, SourceLocation: d.SourceLocation);

    private sealed class MemoryS3() : AmazonS3Client(new AnonymousAWSCredentials(), RegionEndpoint.USEast1)
    {
        private sealed record Item(byte[] Bytes, string Type, string Version, Dictionary<string, string> Metadata);
        private readonly Dictionary<string, List<Item>> values = new(StringComparer.Ordinal);
        private int sequence;
        public bool Versioned = true, FailReceipt, ConflictReceiptOnce, WrongVersionResponse;
        public int OriginalWrites, ReceiptAttempts;
        public string? LastReadVersion;
        private static AmazonS3Exception Error(HttpStatusCode code) => new("S3 protocol double") { StatusCode = code };
        public override Task<GetBucketVersioningResponse> GetBucketVersioningAsync(GetBucketVersioningRequest request, CancellationToken ct = default) =>
            Task.FromResult(new GetBucketVersioningResponse { VersioningConfig = new() { Status = Versioned ? VersionStatus.Enabled : VersionStatus.Suspended } });
        public override async Task<PutObjectResponse> PutObjectAsync(PutObjectRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested(); var receipt = request.Key.EndsWith(".scan.json", StringComparison.Ordinal);
            if (receipt) { ReceiptAttempts++; if (FailReceipt) throw Error(HttpStatusCode.ServiceUnavailable); if (ConflictReceiptOnce) { ConflictReceiptOnce = false; throw Error(HttpStatusCode.PreconditionFailed); } }
            values.TryGetValue(request.Key, out var list);
            if (request.IfNoneMatch == "*" && list?.Count > 0 || request.IfMatch is not null && list?.LastOrDefault()?.Version != request.IfMatch) throw Error(HttpStatusCode.PreconditionFailed);
            using var bytes = new MemoryStream(); await request.InputStream.CopyToAsync(bytes, ct);
            var version = "v" + ++sequence;
            var metadata = request.Metadata.Keys.ToDictionary(k => k.StartsWith("x-amz-meta-", StringComparison.Ordinal) ? k : "x-amz-meta-" + k, k => request.Metadata[k]);
            (list ??= values[request.Key] = []).Add(new(bytes.ToArray(), request.ContentType, version, metadata));
            if (!receipt) OriginalWrites++;
            return new() { VersionId = version, ETag = version };
        }
        private Item Find(string key, string? version) => values.TryGetValue(key, out var list)
            ? (version is null ? list.LastOrDefault() : list.Find(x => x.Version == version)) ?? throw Error(HttpStatusCode.NotFound)
            : throw Error(HttpStatusCode.NotFound);
        public override Task<GetObjectMetadataResponse> GetObjectMetadataAsync(GetObjectMetadataRequest request, CancellationToken ct = default)
        {
            var item = Find(request.Key, request.VersionId); var result = new GetObjectMetadataResponse { VersionId = item.Version, ETag = item.Version };
            result.Headers.ContentType = item.Type; result.Headers.ContentLength = item.Bytes.Length;
            foreach (var (key, value) in item.Metadata) result.Metadata[key] = value;
            return Task.FromResult(result);
        }
        public override Task<GetObjectResponse> GetObjectAsync(GetObjectRequest request, CancellationToken ct = default)
        {
            var item = Find(request.Key, request.VersionId); LastReadVersion = request.VersionId;
            var result = new GetObjectResponse { VersionId = WrongVersionResponse ? "wrong" : item.Version, ETag = item.Version, ResponseStream = new MemoryStream(item.Bytes) };
            result.Headers.ContentType = item.Type; result.Headers.ContentLength = item.Bytes.Length;
            return Task.FromResult(result);
        }
        public void ReplaceLatest(string key, byte[] bytes) { var old = Find(key, null); values[key].Add(old with { Bytes = bytes, Version = "replacement" }); }
        public void Corrupt(string key, string version) { var old = Find(key, version); values[key][values[key].IndexOf(old)] = old with { Bytes = "tampered"u8.ToArray() }; }
        public void Remove(string key, string version) => values[key].RemoveAll(x => x.Version == version);
    }
}
