namespace Monergy.Platform;

public sealed record ComponentMetadata(
    string Component,
    string Runtime,
    string ImplementationState,
    string SourceRevision)
{
    public const string ToolchainOnlyState = "TOOLCHAIN_SCAFFOLD_NO_FEATURES";
    public const string Vs02CandidateState = "VS02_IMPLEMENTATION_CANDIDATE";

    public static ComponentMetadata Create(string component, string implementationState = ToolchainOnlyState) =>
        new(
            component,
            ".NET 10.0.12",
            implementationState,
            Environment.GetEnvironmentVariable("MONERGY_SOURCE_REVISION") ?? "LOCAL_UNCOMMITTED");
}
