using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Monergy.Contracts;

namespace Monergy.Services.CustomerIdentity.Application;

public sealed record AccountAgreementDocument(string Kind, string Version, string Title, string Body, string Sha256);
public sealed record AccountAgreementPublication(string Revision, DateTimeOffset PublishedAt, AccountAgreementDocument[] Documents);
public sealed record AccountAgreementChange(string OperationId, string Revision, bool AcceptTerms, bool AcknowledgePrivacy, bool ConsentToProcessing);
public sealed record AccountAgreementReceipt(string OperationId, string Revision, DateTimeOffset AcceptedAt);
public sealed record AccountAgreementView(string TenantId, string ActorId, string CustomerId,
    AccountAgreementPublication? Publication, AccountAgreementReceipt? Acceptance);

// Operator-published, tenant-specific legal content only. No default/sample legal
// text enters the application, and edits change the content-addressed revision.
public sealed class AccountAgreementCatalog
{
    private static readonly string[] RequiredKinds = ["CONSENT", "PRIVACY", "TERMS"];
    private readonly Dictionary<string, AccountAgreementPublication> publications = new(StringComparer.Ordinal);
    public AccountAgreementCatalog(IConfiguration config)
    {
        foreach (var section in config.GetSection("Monergy:AccountAgreements:Publications").GetChildren())
        {
            var tenant = section["TenantId"] ?? "";
            if (string.IsNullOrWhiteSpace(tenant) || tenant.Length > 128 || tenant.Any(char.IsControl) || publications.ContainsKey(tenant) ||
                !DateTimeOffset.TryParseExact(section["PublishedAt"], "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) || at.Offset != TimeSpan.Zero)
                throw new InvalidOperationException("Invalid account-agreement publication configuration.");
            var docs = section.GetSection("Documents").GetChildren().Select(d => new AccountAgreementDocument(
                d["Kind"] ?? "", d["Version"] ?? "", d["Title"] ?? "", d["Body"] ?? "", d["Sha256"] ?? "")).ToArray();
            if (docs.Length != 3 || !docs.Select(d => d.Kind).Order(StringComparer.Ordinal).SequenceEqual(RequiredKinds) ||
                docs.Any(d => string.IsNullOrWhiteSpace(d.Version) || d.Version.Length > 64 || d.Version.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_')) ||
                    string.IsNullOrWhiteSpace(d.Title) || d.Title.Length > 120 || d.Title.Any(char.IsControl) ||
                    string.IsNullOrWhiteSpace(d.Body) || d.Body.Length > 32_000 || d.Body.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')) ||
                    d.Sha256 != Hash(d.Body)))
                throw new InvalidOperationException("Invalid account-agreement document configuration.");
            docs = docs.OrderBy(d => d.Kind, StringComparer.Ordinal).ToArray();
            var revision = Hash(JsonSerializer.Serialize(new { TenantId = tenant, PublishedAt = at, Documents = docs }));
            publications.Add(tenant, new(revision, at, docs));
        }
    }
    // Return an isolated snapshot so callers cannot change the configured publication.
    public AccountAgreementPublication? Read(string tenant, DateTimeOffset now) =>
        publications.TryGetValue(tenant, out var p) && p.PublishedAt <= now ? p with { Documents = [.. p.Documents] } : null;
    public static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}

public interface ICustomerAccountAgreementRepository
{
    Task<AccountAgreementReceipt?> ReadAsync(string tenant, string actor, string customer, string revision, CancellationToken ct);
    Task<AccountAgreementReceipt> AcceptAsync(string tenant, string actor, string customer, AccountAgreementPublication publication,
        string operationId, DateTimeOffset now, CancellationToken ct);
}

public sealed class CustomerAccountAgreementApplication(ICustomerAccountAgreementRepository repository,
    SelfCustomerProfileApplication profiles, AccountAgreementCatalog catalog, TimeProvider clock)
{
    public async Task<AccountAgreementView> ReadAsync(TenantSessionLookup lookup, CancellationToken ct)
    {
        var owner = await profiles.ReadAsync(lookup, ct).ConfigureAwait(false);
        var publication = catalog.Read(owner.TenantId, clock.GetUtcNow());
        var receipt = publication is null ? null : await repository.ReadAsync(owner.TenantId, owner.ActorId, owner.CustomerId, publication.Revision, ct).ConfigureAwait(false);
        await ConfirmAsync(lookup, owner, ct).ConfigureAwait(false);
        return new(owner.TenantId, owner.ActorId, owner.CustomerId, publication, receipt);
    }
    public async Task<AccountAgreementReceipt> AcceptAsync(TenantSessionLookup lookup, AccountAgreementChange change, CancellationToken ct)
    {
        if (!Guid.TryParseExact(change.OperationId, "N", out var operation) || operation.ToString("N") != change.OperationId ||
            !change.AcceptTerms || !change.AcknowledgePrivacy || !change.ConsentToProcessing)
            throw new TenantAccessException("AGREEMENTS_EXPLICIT_ACCEPTANCE_REQUIRED", 400);
        var owner = await profiles.ReadAsync(lookup, ct).ConfigureAwait(false);
        // PostgreSQL stores microseconds; the returned receipt must equal durable
        // readback rather than differ by sub-microsecond clock precision.
        var instant = clock.GetUtcNow();
        var now = new DateTimeOffset(instant.UtcTicks - instant.UtcTicks % 10, TimeSpan.Zero);
        var publication = catalog.Read(owner.TenantId, now) ?? throw new TenantAccessException("AGREEMENTS_NOT_PUBLISHED", 409);
        if (change.Revision != publication.Revision) throw new TenantAccessException("AGREEMENTS_PUBLICATION_CHANGED", 409);
        // No caller-supplied customer, actor, role, policy text or timestamp.
        var receipt = await repository.AcceptAsync(owner.TenantId, owner.ActorId, owner.CustomerId, publication,
            change.OperationId, now, ct).ConfigureAwait(false);
        await ConfirmAsync(lookup, owner, ct).ConfigureAwait(false);
        return receipt;
    }
    private async Task ConfirmAsync(TenantSessionLookup lookup, SelfCustomerProfile original, CancellationToken ct)
    {
        var owner = await profiles.ReadAsync(lookup, ct).ConfigureAwait(false);
        if (owner.TenantId != original.TenantId || owner.ActorId != original.ActorId || owner.CustomerId != original.CustomerId)
            throw new TenantAccessException("AGREEMENTS_CUSTOMER_CHANGED", 409);
    }
}
