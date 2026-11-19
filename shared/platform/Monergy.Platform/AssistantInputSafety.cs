using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Monergy.Platform;

public static partial class AssistantInputSafety
{
    public static string Normalize(string value) => value.Normalize(NormalizationForm.FormKC);
    public static bool ContainsProtectedMaterial(string value)
    {
        if (value.Any(c => char.GetUnicodeCategory(c) == UnicodeCategory.Format)) return true;
        return Protected().IsMatch(Normalize(value));
    }
    public static bool ContainsUnsafeInstruction(string value) => Unsafe().IsMatch(Normalize(value));
    [GeneratedRegex(@"(?i)(?:\b(?:password|passwd|secret|token|api[_ -]?key|client[_ -]?secret)\s*[:=]\s*\S+|bearer\s+\S+|eyJ[A-Za-z0-9_-]{15}|-----BEGIN|\bAKIA[A-Z0-9]{16}\b|[^\s@]+@[^\s@]+|\b[ACT]\d{3}\b|\b\d{6,}\b|(?:https?://|www\.))", RegexOptions.CultureInvariant)]
    private static partial Regex Protected();
    [GeneratedRegex(@"(?i)(?:ignore\s+(?:all\s+)?(?:previous|prior|system)\s+instructions|(?:reveal|show|print)\s+(?:the\s+)?(?:system\s+prompt|credentials|secrets)|\b(?:bypass|override)\s+(?:authorization|permission|consent|security))", RegexOptions.CultureInvariant)]
    private static partial Regex Unsafe();
}
