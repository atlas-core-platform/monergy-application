namespace Monergy.Contracts;

public static class D14ContractNames
{
    public const string CustomerIdentityChanged = "CustomerIdentityChanged";
}

// ChangeKind is bounded reference vocabulary for the LOCAL/CI simulator. It is
// not a normative architecture enumeration.
public sealed record CustomerIdentityChangedPayload(
    string ChangeKind,
    int Revision);

