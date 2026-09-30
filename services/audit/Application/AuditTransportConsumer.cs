using Monergy.Contracts;
using Monergy.Platform;

namespace Monergy.Services.Audit.Application;

public sealed class AuditTransportConsumer(AuditApplication application) : IGovernedEventConsumer<AuditableEvent>
{
    public async Task<bool> ConsumeAsync(AuditableEvent source, CancellationToken cancellationToken) =>
        (await application.ConsumeWithDispositionAsync(source, cancellationToken)).Created;
}
