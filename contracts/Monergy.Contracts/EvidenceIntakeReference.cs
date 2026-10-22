namespace Monergy.Contracts;

public static class EvidenceIntakeReference
{
    public const string DurablePrefix = "s3-intake://";
    public static bool Matches(string reference, string operationId) =>
        reference == "reference://customer-intake/" + operationId || reference == DurablePrefix + operationId;
    public static bool IsProduct(string reference) => reference.StartsWith("reference://customer-intake/", StringComparison.Ordinal) ||
        reference.StartsWith(DurablePrefix, StringComparison.Ordinal);
}
