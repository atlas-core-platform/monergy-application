namespace Monergy.Platform;

// A dispatched mutation may have committed even when its acknowledgement is unavailable.
// Never attach transport/SQL messages, request text or credentials to this boundary exception.
public sealed class AssistantOutcomeUnknownException : Exception
{
    public AssistantOutcomeUnknownException() : base("The Assistant operation outcome could not be confirmed.") { }
}
