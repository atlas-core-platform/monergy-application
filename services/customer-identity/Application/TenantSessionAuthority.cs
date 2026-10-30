using System.Security.Cryptography;
using Monergy.Contracts;
using Monergy.Platform;

namespace Monergy.Services.CustomerIdentity.Application;

public interface ITenantMembershipClient
{
    Task<TenantMembershipState> ReadAsync(string tenantId, string actorId, CancellationToken cancellationToken);
}

public interface ITenantSessionRepository
{
    Task<TenantSessionContext> CreateAsync(string tenantId, string actorId, string sessionId, long subjectVersion, CancellationToken cancellationToken);
    Task<TenantSessionContext> CreateAsync(string tenantId, string actorId, string sessionId, long subjectVersion,
        SessionClientDescriptor client, CancellationToken cancellationToken) =>
        CreateAsync(tenantId, actorId, sessionId, subjectVersion, cancellationToken);
    Task<TenantSessionContext?> ReadCurrentAsync(string tenantId, string sessionId, CancellationToken cancellationToken);
    Task RevokeAsync(string tenantId, string sessionId, CancellationToken cancellationToken);
    Task<AccessManagementReceipt> ConsumeAsync(string trustedTenant, string destination, AccessManagementEvent message, CancellationToken cancellationToken);
    Task<bool> ReadyAsync(CancellationToken cancellationToken);
}

public sealed record SessionClientDescriptor(string DeviceClass, string ClientFamily)
{
    public static SessionClientDescriptor Unknown { get; } = new("Unknown", "Unknown");
    public static SessionClientDescriptor FromHeaders(string? deviceClass, string? clientFamily)
    {
        var device = deviceClass is "Desktop" or "Tablet" or "Mobile" ? deviceClass : "Unknown";
        var client = clientFamily is "Chrome" or "Edge" or "Firefox" or "Safari" or "Other" ? clientFamily : "Unknown";
        return new(device, client);
    }
}

public sealed class ReferenceTenantAuthenticator
{
    private readonly Identity[] identities;

    public ReferenceTenantAuthenticator(IConfiguration configuration)
    {
        TenantAccessIntegration.EnsureAllowed(configuration);
        var values = configuration.GetSection("Monergy:AccessIntegration:Identities").GetChildren().Select(section =>
            new Identity(section["TenantId"] ?? "", section["ActorId"] ?? "", section["Token"] ?? "")).ToArray();
        if (values.Length is < 1 or > 100) throw new InvalidOperationException("Bounded reference identities are required.");
        foreach (var value in values)
        {
            TenantAccessProtocol.Identifier(value.TenantId, 64);
            TenantAccessProtocol.Identifier(value.ActorId);
            TenantAccessIntegration.ValidateToken(value.Token);
        }
        if (values.Select(value => (value.TenantId, value.Token)).Distinct().Count() != values.Length)
            throw new InvalidOperationException("Reference identities must have unambiguous tenant/token bindings.");
        identities = values;
    }

    public string Authenticate(string tenantId, string token)
    {
        TenantAccessProtocol.Identifier(tenantId, 64);
        var identity = identities.SingleOrDefault(value => value.TenantId == tenantId && TenantAccessIntegration.TokenMatches(token, value.Token));
        return identity?.ActorId ?? throw new TenantAccessException("AUTHENTICATION_REQUIRED", 401);
    }

    private sealed record Identity(string TenantId, string ActorId, string Token);
}

public sealed class TenantSessionAuthority(ITenantSessionRepository repository, ITenantMembershipClient membership, ReferenceTenantAuthenticator authenticator)
{
    public async Task<TenantSessionContext> EstablishAsync(string tenantId, string authenticationReference, CancellationToken cancellationToken)
        => await EstablishAsync(tenantId, authenticationReference, SessionClientDescriptor.Unknown, cancellationToken).ConfigureAwait(false);

    public async Task<TenantSessionContext> EstablishAsync(string tenantId, string authenticationReference,
        SessionClientDescriptor client, CancellationToken cancellationToken)
    {
        var actorId = authenticator.Authenticate(tenantId, authenticationReference);
        return await EstablishVerifiedAsync(tenantId, actorId, client, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<TenantSessionContext> EstablishVerifiedAsync(string tenantId, string actorId, CancellationToken cancellationToken)
        => await EstablishVerifiedAsync(tenantId, actorId, SessionClientDescriptor.Unknown, cancellationToken).ConfigureAwait(false);

    internal async Task<TenantSessionContext> EstablishVerifiedAsync(string tenantId, string actorId,
        SessionClientDescriptor client, CancellationToken cancellationToken)
    {
        TenantAccessProtocol.Identifier(tenantId, 64);
        TenantAccessProtocol.Identifier(actorId);
        client = SessionClientDescriptor.FromHeaders(client.DeviceClass, client.ClientFamily);
        var state = await membership.ReadAsync(tenantId, actorId, cancellationToken).ConfigureAwait(false);
        if (!state.Active) throw new TenantAccessException("MEMBERSHIP_INACTIVE", 401);
        var sessionId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var session = await repository.CreateAsync(tenantId, actorId, sessionId, state.SubjectVersion, client, cancellationToken).ConfigureAwait(false);
        // Membership is re-read after the owner commit. Cross-service transactions
        // are not implied; every later validation and protected decision is fresh.
        try
        {
            var confirmed = await membership.ReadAsync(tenantId, actorId, cancellationToken).ConfigureAwait(false);
            if (!confirmed.Active || confirmed.SubjectVersion != session.SubjectVersion)
                throw new TenantAccessException("MEMBERSHIP_CHANGED", 409);
            return session;
        }
        catch
        {
            await repository.RevokeAsync(tenantId, sessionId, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<bool> EndAsync(TenantSessionLookup lookup, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        TenantAccessProtocol.Identifier(lookup.TenantId, 64);
        TenantAccessProtocol.Identifier(lookup.AuthenticationContextId, 160);
        // Possession only authorizes ending this exact tenant-scoped session.
        // Idempotent revocation remains available after expiry or role removal.
        await repository.RevokeAsync(lookup.TenantId, lookup.AuthenticationContextId, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<TenantSessionContext> ValidateAsync(TenantSessionLookup lookup, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        TenantAccessProtocol.Identifier(lookup.TenantId, 64);
        TenantAccessProtocol.Identifier(lookup.AuthenticationContextId, 160);
        var session = await repository.ReadCurrentAsync(lookup.TenantId, lookup.AuthenticationContextId, cancellationToken).ConfigureAwait(false)
            ?? throw new TenantAccessException("SESSION_NOT_CURRENT", 401);
        var state = await membership.ReadAsync(session.TenantId, session.ActorId, cancellationToken).ConfigureAwait(false);
        if (!state.Active || state.SubjectVersion != session.SubjectVersion)
        {
            await repository.RevokeAsync(session.TenantId, session.AuthenticationContextId, cancellationToken).ConfigureAwait(false);
            throw new TenantAccessException("SESSION_NOT_CURRENT", 401);
        }
        return await repository.ReadCurrentAsync(lookup.TenantId, lookup.AuthenticationContextId, cancellationToken).ConfigureAwait(false)
            ?? throw new TenantAccessException("SESSION_NOT_CURRENT", 401);
    }
}
