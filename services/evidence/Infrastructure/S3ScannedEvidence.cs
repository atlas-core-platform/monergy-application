using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using Monergy.Contracts;
using Monergy.Services.Evidence.Application;

namespace Monergy.Services.Evidence.Infrastructure;

public sealed partial class S3EvidenceContentStore
{
    private const int ContentLimit = 1_048_576;
    private static string IntakeKey(string reference)
    {
        var id = reference.StartsWith(EvidenceIntakeReference.DurablePrefix, StringComparison.Ordinal)
            ? reference[EvidenceIntakeReference.DurablePrefix.Length..] : "";
        if (id.Length != 32 || id.Any(c => !char.IsAsciiHexDigit(c) || char.IsAsciiLetterUpper(c)))
            throw new EvidenceStorageUnavailableException();
        return "evidence/intake/" + id + "/original";
    }
    private static bool Conditional(AmazonS3Exception e) => e.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict;
    private static bool Missing(AmazonS3Exception e) => e.StatusCode == HttpStatusCode.NotFound;

    public async Task CheckReadyAsync(CancellationToken ct)
    {
        var result = await client.GetBucketVersioningAsync(new GetBucketVersioningRequest { BucketName = bucket }, ct);
        if (result.VersioningConfig?.Status != VersionStatus.Enabled) throw new EvidenceStorageUnavailableException();
    }

    public async Task<EvidenceContentDescriptor> StageScannedAsync(string operationId, byte[] bytes, string contentType,
        EvidenceMalwareCheck check, EvidenceSourceLocation? source, CancellationToken ct)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (bytes.Length is < 1 or > ContentLimit || !EvidenceMalwarePolicy.Verified(check, hash, DateTimeOffset.UtcNow))
            throw new EvidenceScanUnavailableException();
        var reference = EvidenceIntakeReference.DurablePrefix + operationId;
        var key = IntakeKey(reference);
        try
        {
            // Product storage is provisioned by the operator. Do not create or reconfigure buckets at runtime.
            var versioning = await client.GetBucketVersioningAsync(new GetBucketVersioningRequest { BucketName = bucket }, ct);
            if (versioning.VersioningConfig?.Status != VersionStatus.Enabled) throw new EvidenceStorageUnavailableException();
            GetObjectMetadataResponse? existing = null;
            try { existing = await client.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = bucket, Key = key }, ct); }
            catch (AmazonS3Exception e) when (Missing(e)) { }
            string version;
            if (existing is null)
            {
                try
                {
                    using var stream = new MemoryStream(bytes, false);
                    var put = await client.PutObjectAsync(new PutObjectRequest
                    {
                        BucketName = bucket, Key = key, InputStream = stream, ContentType = contentType, IfNoneMatch = "*",
                        Metadata = { ["monergy-sha256"] = hash, ["monergy-reference"] = reference },
                    }, ct);
                    version = put.VersionId ?? throw new EvidenceStorageUnavailableException();
                }
                catch (AmazonS3Exception e) when (Conditional(e))
                {
                    existing = await client.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = bucket, Key = key }, ct);
                    version = existing.VersionId;
                }
            }
            else version = existing.VersionId;
            if (string.IsNullOrWhiteSpace(version) || version == "null") throw new EvidenceStorageUnavailableException();
            if (existing is not null && (existing.Metadata["x-amz-meta-monergy-sha256"] != hash ||
                existing.Headers.ContentLength != bytes.LongLength || existing.Headers.ContentType != contentType))
                throw new InvalidOperationException("Evidence operation identifies different content.");
            var descriptor = new EvidenceContentDescriptor(reference, hash, contentType, bytes.LongLength, key, version, check, source);
            // Confirm retained bytes before accepting a retry or publishing the receipt.
            var retained = await ReadExactAsync(descriptor, ct);
            if (retained is null || !retained.AsSpan().SequenceEqual(bytes)) throw new EvidenceStorageUnavailableException();
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var prior = await ReadReceiptAsync(reference, ct);
                if (prior.Descriptor is { } old && (old.Sha256 != hash || old.ContentType != contentType ||
                    old.ContentLength != bytes.LongLength || old.StorageVersionId != version || old.SourceLocation != source))
                    throw new InvalidOperationException("Evidence operation identifies different content or source.");
                using var receipt = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(descriptor, ContractJson.Options), false);
                try
                {
                    await client.PutObjectAsync(new PutObjectRequest
                    {
                        BucketName = bucket, Key = key + ".scan.json", InputStream = receipt, ContentType = "application/json",
                        IfNoneMatch = prior.ETag is null ? "*" : null, IfMatch = prior.ETag,
                    }, ct);
                    return descriptor;
                }
                catch (AmazonS3Exception e) when (Conditional(e)) { }
            }
            throw new EvidenceStorageUnavailableException();
        }
        catch (AmazonS3Exception) { throw new EvidenceStorageUnavailableException(); }
        catch (HttpRequestException) { throw new EvidenceStorageUnavailableException(); }
        catch (IOException) { throw new EvidenceStorageUnavailableException(); }
    }

    private async Task<(EvidenceContentDescriptor? Descriptor, string? ETag)> ReadReceiptAsync(string reference, CancellationToken ct)
    {
        var key = IntakeKey(reference);
        try
        {
            using var response = await client.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key + ".scan.json" }, ct);
            var bytes = await BoundedReadAsync(response.ResponseStream, 16_384, ct);
            var item = JsonSerializer.Deserialize<EvidenceContentDescriptor>(bytes, ContractJson.Options);
            if (item is null || item.Reference != reference || item.StorageKey != key || string.IsNullOrWhiteSpace(item.StorageVersionId) ||
                item.StorageVersionId == "null" || item.ContentLength is < 1 or > ContentLimit || string.IsNullOrWhiteSpace(response.ETag) ||
                item.MalwareCheck is null || !EvidenceMalwarePolicy.Verified(item.MalwareCheck, item.Sha256, item.MalwareCheck.CheckedAt))
                throw new EvidenceStorageUnavailableException();
            var original = await client.GetObjectMetadataAsync(new GetObjectMetadataRequest
                { BucketName = bucket, Key = key, VersionId = item.StorageVersionId }, ct);
            if (original.VersionId != item.StorageVersionId || original.Headers.ContentLength != item.ContentLength ||
                original.Headers.ContentType != item.ContentType || original.Metadata["x-amz-meta-monergy-sha256"] != item.Sha256)
                throw new EvidenceStorageUnavailableException();
            return (item, response.ETag);
        }
        catch (AmazonS3Exception e) when (Missing(e)) { return (null, null); }
        catch (Exception e) when (e is AmazonS3Exception or HttpRequestException or IOException or JsonException)
        { throw new EvidenceStorageUnavailableException(); }
    }

    public Task<byte[]?> ReadRegisteredAsync(EvidenceVersionRecord version, CancellationToken ct)
    {
        // Processing uses the exact version persisted by Evidence, never the latest S3 object/scan.
        if (version.ContentStorageKey != IntakeKey(version.ContentReference) || string.IsNullOrWhiteSpace(version.ContentStorageVersionId))
            throw new EvidenceStorageUnavailableException();
        return ReadExactAsync(new(version.ContentReference, version.ContentSha256, version.ContentType, version.ContentLength,
            version.ContentStorageKey, version.ContentStorageVersionId), ct);
    }
    private async Task<byte[]?> ReadExactAsync(EvidenceContentDescriptor descriptor, CancellationToken ct)
    {
        try
        {
            using var response = await client.GetObjectAsync(new GetObjectRequest
            { BucketName = bucket, Key = descriptor.StorageKey, VersionId = descriptor.StorageVersionId }, ct);
            if (response.VersionId != descriptor.StorageVersionId || response.Headers.ContentType != descriptor.ContentType ||
                response.Headers.ContentLength != descriptor.ContentLength || descriptor.ContentLength is < 1 or > ContentLimit)
                throw new EvidenceStorageUnavailableException();
            var bytes = await BoundedReadAsync(response.ResponseStream, ContentLimit, ct);
            if (bytes.LongLength != descriptor.ContentLength || Convert.ToHexStringLower(SHA256.HashData(bytes)) != descriptor.Sha256)
                throw new EvidenceStorageUnavailableException();
            return bytes;
        }
        catch (AmazonS3Exception e) when (Missing(e)) { return null; }
        catch (Exception e) when (e is AmazonS3Exception or HttpRequestException or IOException)
        { throw new EvidenceStorageUnavailableException(); }
    }
    private static async Task<byte[]> BoundedReadAsync(Stream stream, int limit, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16_384];
        while (true)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(0, Math.Min(chunk.Length, limit + 1 - (int)buffer.Length)), ct);
            if (read == 0) return buffer.ToArray();
            await buffer.WriteAsync(chunk.AsMemory(0, read), ct);
            if (buffer.Length > limit) throw new EvidenceStorageUnavailableException();
        }
    }
}
