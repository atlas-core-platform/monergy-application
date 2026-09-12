namespace Monergy.Platform;

public sealed record ComponentMetadata(
    string Component,
    string Runtime,
    string ImplementationState,
    string SourceRevision)
{
    public const string ToolchainOnlyState = "TOOLCHAIN_SCAFFOLD_NO_FEATURES";

    public static ComponentMetadata Create(string component) =>
        new(
            component,
            ".NET 10.0.12",
            ToolchainOnlyState,
            Environment.GetEnvironmentVariable("MONERGY_SOURCE_REVISION") ?? "LOCAL_UNCOMMITTED");
}
