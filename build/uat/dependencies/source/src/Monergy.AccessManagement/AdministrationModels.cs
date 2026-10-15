using System.Text.Json;

namespace Monergy.AccessManagement;

public sealed record PermissionWrite(long ExpectedPolicyVersion, string Code, string Label, CapabilityGrant[] Grants);
public sealed record RoleWrite(long ExpectedPolicyVersion, string Code, string Label, string[] PermissionIds);
public sealed record MemberCreate(long ExpectedPolicyVersion, string ActorId);
public sealed record MemberWrite(long ExpectedPolicyVersion, string? BusinessRoleId, bool Active, bool TenantAdmin);
public sealed record ResourceGrantWrite(long ExpectedPolicyVersion, string ActorId, string CapabilityId, string ResourceType, string ResourceId);
public sealed record PolicyRevision(long ExpectedPolicyVersion);
public sealed record DefaultAccessPackChange(string Kind, string ObjectId, string? Before, string? After);
public sealed record DefaultAccessPackPlan(
    string? InstalledVersion, string TargetVersion, long PolicyVersion, string PlanHash,
    bool AlreadyInstalled, IReadOnlyList<DefaultAccessPackChange> Changes, IReadOnlyList<string> Conflicts);
public sealed record DefaultAccessPackAdoptionRequest(long ExpectedPolicyVersion, string OperationId, string PlanHash);
public sealed record DefaultAccessPackStatus(
    string? InstalledVersion,
    string? InstalledIdentity,
    DateTimeOffset? InstalledAt,
    long PolicyVersion,
    string? LatestOperationId,
    string? LatestPlanHash,
    long? FromPolicyVersion,
    long? ToPolicyVersion);
public sealed record DefaultAccessPackAdoptionResult(
    string PackVersion, long PolicyVersion, string OperationId, string PlanHash, bool Replayed);
public sealed record AdministrationResult(string Id, long PolicyVersion, long? SubjectVersion);
public sealed record AdministrationPage(long PolicyVersion, IReadOnlyList<JsonElement> Items, string? NextCursor);
public enum AdministrationEntity { Permissions, Roles, Members, ResourceGrants, Groups, Imports, CustomerRelationships }

public sealed class AccessAdministrationException(string code, int statusCode) : Exception(code)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

public static class AccessInput
{
    public static void Identifier(string? value, int max = 128)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > max ||
            !char.IsAsciiLetterOrDigit(value[0]) ||
            value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or ':')))
            throw new AccessAdministrationException("INVALID_IDENTIFIER", 400);
    }

    public static string Code(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 80 ||
            !char.IsAsciiLetter(value[0]) || value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-')))
            throw new AccessAdministrationException("INVALID_CODE", 400);
        return value.ToLowerInvariant();
    }

    public static string Label(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 120 || value.Any(char.IsControl))
            throw new AccessAdministrationException("INVALID_LABEL", 400);
        return value.Trim();
    }

    public static void Require(bool condition, string code, int status = 400)
    {
        if (!condition) throw new AccessAdministrationException(code, status);
    }
}
