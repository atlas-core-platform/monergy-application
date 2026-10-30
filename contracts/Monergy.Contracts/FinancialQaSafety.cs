using System.Text;
using System.Text.RegularExpressions;

namespace Monergy.Contracts;

public static partial class FinancialQaSafety
{
    public const string PromptVersion = "monergy-basic-qa/4";

    public static bool ContainsInstructionOverride(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var canonical = Canonicalize(value);
        return OverrideInstruction().IsMatch(canonical) || AuthorityMessage().IsMatch(canonical) ||
            PromptDisclosure().IsMatch(canonical) || ToolExecution().IsMatch(canonical) ||
            SecretExfiltration().IsMatch(canonical);
    }

    private static string Canonicalize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Normalize(NormalizationForm.FormKC))
            builder.Append(char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : ' ');
        return Whitespace().Replace(builder.ToString(), " ").Trim();
    }

    [GeneratedRegex(@"\b(ignore|disregard|forget|override|bypass)\b(?: [a-z0-9]+){0,8} \b(instruction|instructions|system|developer|prompt|policy|policies|guardrail|guardrails|rules)\b", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex OverrideInstruction();

    [GeneratedRegex(@"\b(system|developer|assistant)\b(?: [a-z0-9]+){0,2} \b(message|messages|prompt|prompts|instruction|instructions)\b", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex AuthorityMessage();

    [GeneratedRegex(@"\b(reveal|repeat|print|display|show|return|expose)\b(?: [a-z0-9]+){0,8} \b(system|developer)\b(?: [a-z0-9]+){0,3} \b(prompt|message|instruction|instructions)\b", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex PromptDisclosure();

    [GeneratedRegex(@"\b(call|invoke|execute|run)\b(?: [a-z0-9]+){0,4} \b(tool|tools|function|functions|command|commands|shell|terminal)\b", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex ToolExecution();

    [GeneratedRegex(@"\b(reveal|print|send|exfiltrate|leak)\b(?: [a-z0-9]+){0,8} \b(secret|secrets|token|tokens|credential|credentials|password|passwords|api key|api keys)\b", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex SecretExfiltration();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex Whitespace();
}
