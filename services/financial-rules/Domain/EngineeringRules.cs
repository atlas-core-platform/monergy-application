using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Monergy.Contracts;

namespace Monergy.Services.FinancialRules.Domain;

public sealed record RuleDefinition(
    string RuleId,
    string Version,
    string ImplementationIdentity,
    string NumericSemantics,
    ImmutableArray<string> Roles)
{
    public string DefinitionHash => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join('\n', RuleId, Version, ImplementationIdentity, NumericSemantics, string.Join(',', Roles)))));
}

public sealed record EvaluatedValue(decimal Value, string Unit, string Operation);

public sealed class CalculationException(string code, ContractErrorCategory category) : Exception(code)
{
    public string Code { get; } = code;
    public ContractErrorCategory Category { get; } = category;
}

public interface IDeterministicRule
{
    RuleDefinition Definition { get; }
    EvaluatedValue Evaluate(ImmutableArray<CalculationInputLineage> inputs);
}

public interface IRuleRegistry
{
    IDeterministicRule? Find(string ruleId, string version);
}

// Three reviewed typed fixtures. This is not a client formula registry or expression interpreter.
public sealed class EngineeringRule : IDeterministicRule
{
    public EngineeringRule(string ruleId, string version)
    {
        if (!((ruleId == "engineering.sum" && version is "1.0.0" or "2.0.0") ||
            (ruleId == "engineering.ratio" && version == "1.0.0")))
        {
            throw new ArgumentException("Only reviewed engineering fixture identities are supported.", nameof(ruleId));
        }

        Definition = new RuleDefinition(
            ruleId,
            version,
            $"{typeof(EngineeringRule).FullName}/{version}/{ImplementationDigest}",
            ruleId == "engineering.ratio"
                ? "decimal; checked; left/right; same explicit currency; ratio rounded to 4 decimals ToEven; fixture only"
                : "decimal; checked; ordinal roles left,right; same explicit currency; exact addition; v2 doubles sum; fixture only",
            ["left", "right"]);
    }

    private static string ImplementationDigest { get; } =
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(EngineeringRule).Assembly.Location)));

    public RuleDefinition Definition { get; }

    public EvaluatedValue Evaluate(ImmutableArray<CalculationInputLineage> inputs)
    {
        if (inputs.IsDefault || inputs.Length != 2 ||
            !inputs.Select(input => input.Role).Order(StringComparer.Ordinal).SequenceEqual(Definition.Roles))
        {
            throw new CalculationException("calculation.inputs.invalid", ContractErrorCategory.ValidationError);
        }

        var left = inputs.Single(input => input.Role == "left");
        var right = inputs.Single(input => input.Role == "right");
        if (string.IsNullOrWhiteSpace(left.Unit) || left.Unit.Length != 3 ||
            !left.Unit.All(character => character is >= 'A' and <= 'Z') ||
            !string.Equals(left.Unit, right.Unit, StringComparison.Ordinal))
        {
            throw new CalculationException("calculation.units.incompatible", ContractErrorCategory.ValidationError);
        }

        try
        {
            if (Definition.RuleId == "engineering.ratio")
            {
                if (right.Value == 0)
                {
                    throw new CalculationException("calculation.divisor.zero", ContractErrorCategory.ProcessingFailed);
                }

                return new EvaluatedValue(decimal.Round(checked(left.Value / right.Value), 4, MidpointRounding.ToEven), "ratio", "left / right; ToEven(4)");
            }

            var factor = Definition.Version == "2.0.0" ? 2m : 1m;
            return new EvaluatedValue(checked((left.Value + right.Value) * factor), left.Unit,
                "(left + right) * " + factor.ToString(CultureInfo.InvariantCulture));
        }
        catch (OverflowException)
        {
            throw new CalculationException("calculation.numeric.overflow", ContractErrorCategory.ProcessingFailed);
        }
    }
}
