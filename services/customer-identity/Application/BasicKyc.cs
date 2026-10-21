using Monergy.Contracts;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Monergy.Platform;

namespace Monergy.Services.CustomerIdentity.Application;

public sealed record KycReviewer(string ActorId, string Name);
public sealed record KycPolicy(string Revision, string Notice, KycReviewer[] Reviewers);
public sealed record KycEvidence(string DocumentVersionId, string Purpose, string FileName, string Sha256);
public sealed record KycChange(string OperationId, long ExpectedVersion, string Action, string? ReviewerId, string? PolicyRevision, bool Consent, string Reason, KycEvidence[] Evidence);
public sealed record KycCase(string TenantId, string CustomerId, string CustomerName, string OwnerActorId, long Version,
    string State, string ReviewerId, string ReviewerName, string PolicyRevision, string Notice, KycEvidence[] Evidence,
    string Reason, string RecordedBy, DateTimeOffset RecordedAt, string OperationId);
public sealed record KycPage(KycCase[] Items, string? NextAfter);
public interface IBasicKycRepository
{
    Task<KycCase?> ReadAsync(string tenant, string customer, CancellationToken ct);
    Task<KycCase[]> HistoryAsync(string tenant, string customer, long before, CancellationToken ct);
    Task<KycCase[]> QueueAsync(string tenant, string reviewer, string after, CancellationToken ct);
    Task<KycCase> ChangeAsync(string tenant, string actor, string customer, KycChange change,
        Func<KycCase?, KycCase> transition, CancellationToken ct);
}
public sealed class BasicKycPolicy(IConfiguration config)
{
    public KycPolicy? Read(string tenant)
    {
        var section = config.GetSection("Monergy:BasicKyc:Tenants:" + tenant);
        if (section["Enabled"] != "true") return null;
        var notice = section["Notice"] ?? "";
        var reviewers = section.GetSection("Reviewers").GetChildren().Select(x => new KycReviewer(x["ActorId"] ?? "", x["Name"] ?? "")).ToArray();
        if (notice.Length is < 20 or > 4000 || reviewers.Length is < 1 or > 20 || reviewers.Select(x => x.ActorId).Distinct().Count() != reviewers.Length ||
            reviewers.Any(x => !TenantAccessClient.Identifier(x.ActorId, 128) || !BasicKyc.Text(x.Name, 120)))
            throw new TenantAccessException("KYC_CONFIGURATION_INVALID", 503);
        var revision = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { notice, reviewers }))));
        return new(revision, notice, reviewers);
    }
}
public static class BasicKyc
{
    public static bool Text(string value, int limit) => !string.IsNullOrWhiteSpace(value) && value == value.Trim() && value.Length <= limit && !value.Any(char.IsControl);
    public static void Validate(KycChange c)
    {
        if (!Guid.TryParseExact(c.OperationId, "N", out _) || c.OperationId.Any(char.IsAsciiLetterUpper) || c.ExpectedVersion is < 0 or > 9007199254740000 ||
            c.Action is not ("SUBMIT" or "WITHDRAW" or "ACCEPT" or "REQUEST_CHANGES" or "REJECT") || !Text(c.Reason, 500) || c.Evidence is null)
            throw new TenantAccessException("KYC_CHANGE_INVALID", 400);
        if (c.Action == "SUBMIT")
        {
            if (!c.Consent || !TenantAccessClient.Identifier(c.ReviewerId, 128) || c.PolicyRevision?.Length != 64 || c.Evidence.Length is < 1 or > 3 ||
                c.Evidence.Any(x => x is null) ||
                c.Evidence.Select(x => x.DocumentVersionId).Distinct().Count() != c.Evidence.Length || !c.Evidence.Any(x => x.Purpose == "IDENTITY") ||
                c.Evidence.Any(x => !TenantAccessClient.Identifier(x.DocumentVersionId, 160) || x.Purpose is not ("IDENTITY" or "ADDRESS") || !Text(x.FileName, 255) || x.Sha256.Length != 64 || x.Sha256.Any(ch => !char.IsAsciiHexDigit(ch) || char.IsAsciiLetterUpper(ch))))
                throw new TenantAccessException("KYC_SUBMISSION_INVALID", 400);
        }
        else if (c.Evidence.Length != 0 || c.Consent || c.ReviewerId is not null || c.PolicyRevision is not null)
            throw new TenantAccessException("KYC_CHANGE_INVALID", 400);
    }
    public static KycCase Transition(KycCase? current, string tenant, string customer, string name, string owner, string actor, KycChange change, KycPolicy? policy, DateTimeOffset now)
    {
        Validate(change);
        if ((current?.Version ?? 0) != change.ExpectedVersion) throw new TenantAccessException("KYC_VERSION_CONFLICT", 409);
        if (change.Action == "SUBMIT")
        {
            if (actor != owner || current?.State == "SUBMITTED") throw new TenantAccessException("KYC_TRANSITION_DENIED", 409);
            var reviewer = policy?.Reviewers.SingleOrDefault(x => x.ActorId == change.ReviewerId);
            if (reviewer is null || reviewer.ActorId == actor || policy!.Revision != change.PolicyRevision) throw new TenantAccessException("KYC_POLICY_CHANGED", 409);
            return new(tenant, customer, name, owner, change.ExpectedVersion + 1, "SUBMITTED", reviewer.ActorId, reviewer.Name, policy.Revision, policy.Notice, change.Evidence, change.Reason, actor, now, change.OperationId);
        }
        if (current is null || current.State != "SUBMITTED") throw new TenantAccessException("KYC_TRANSITION_DENIED", 409);
        if (change.Action == "WITHDRAW" ? actor != owner : actor == owner || actor != current.ReviewerId || policy?.Revision != current.PolicyRevision || !policy.Reviewers.Any(x => x.ActorId == actor))
            throw new TenantAccessException("KYC_REVIEW_DENIED", 403);
        return current with { Version = current.Version + 1, State = change.Action switch { "WITHDRAW" => "WITHDRAWN", "ACCEPT" => "MANUAL_REVIEW_ACCEPTED", "REQUEST_CHANGES" => "CHANGES_REQUESTED", _ => "REJECTED" }, Reason = change.Reason, RecordedBy = actor, RecordedAt = now, OperationId = change.OperationId };
    }
}
