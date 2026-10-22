namespace Monergy.Services.Evidence.Application;

public interface IEvidenceIntakeStore : IEvidenceContentStore
{
    Task<EvidenceContentDescriptor> StageScannedAsync(string operationId, byte[] bytes, string contentType,
        EvidenceMalwareCheck check, EvidenceSourceLocation? source, CancellationToken ct);
    Task<byte[]?> ReadRegisteredAsync(EvidenceVersionRecord version, CancellationToken ct);
}
public sealed class EvidenceStorageUnavailableException : Exception;
