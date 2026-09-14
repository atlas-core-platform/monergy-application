using Monergy.Platform;
using Monergy.Services.DocumentIntelligence;
using Monergy.Services.DocumentIntelligence.Application;

const string serviceName = "Document Intelligence Service";

var builder = Host.CreateApplicationBuilder(args);
builder.AddMonergyPlatform(serviceName);
builder.Services.AddHealthChecks();
if (ReferenceAdapterGuard.IsSelected(builder.Configuration))
{
    builder.Services.AddDocumentIntelligenceReferenceAdapters(
        builder.Configuration,
        new UnconfiguredEvidenceContentReader());
}
builder.Services.AddHostedService<StartupWorker>();

await builder.Build().RunAsync();

internal sealed class UnconfiguredEvidenceContentReader : IEvidenceContentReader
{
    public Task<EvidenceContent?> ReadAsync(
        string documentVersionId,
        string customerId,
        Monergy.Contracts.TrustedSecurityContext security,
        string correlationId,
        CancellationToken cancellationToken) =>
        Task.FromResult<EvidenceContent?>(null);
}
