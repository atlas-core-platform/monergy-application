namespace Monergy.Contracts;

public static class D13ContractNames
{
    public const string GetCustomer = "GetCustomer";
    public const string GetTrustedActorContext = "GetTrustedActorContext";
    public const string RegisterOrUpdateCustomer = "Register/UpdateCustomer";
    public const string RecordKycResult = "RecordKycResult";
}

public sealed record GetCustomer(string CustomerId);

public sealed record CustomerProjection(
    string CustomerId,
    string DisplayName,
    int Revision,
    DateTimeOffset UpdatedAt);

public sealed record GetTrustedActorContext(
    string CustomerId,
    string AuthenticationReference);

public sealed record RegisterOrUpdateCustomer(
    string CustomerId,
    string DisplayName,
    int? ExpectedRevision);

public sealed record RecordKycResult(
    string CustomerId,
    string VerificationId,
    string StatusCode,
    DateTimeOffset DeterminedAt);

public sealed record KycVerification(
    string CustomerId,
    string VerificationId,
    string StatusCode,
    DateTimeOffset DeterminedAt,
    int Sequence,
    DateTimeOffset RecordedAt);
