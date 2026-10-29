namespace Monergy.Platform;

// Customer source categories are presentation labels; owner contracts retain canonical object types.
public static class FinancialSourceFactBoundary
{
    public static string ForOwner(string customerType, string? extractionVersion)
    {
        if (customerType == "INSURANCE") return "INSURANCE_POLICY";
        if (customerType == "TAX") return "TAX_RECORD";
        if (customerType != "ASSET") return customerType;
        return extractionVersion switch
        {
            "property-sale-deed-png-ocr/1.0.0" or
            "property-sale-deed-jpeg-ocr/1.0.0" or
            "property-sale-deed-pdf-ocr/1.0.0" or
            "property-registration-png-ocr/1.0.0" or
            "property-registration-jpeg-ocr/1.0.0" or
            "property-registration-pdf-ocr/1.0.0" => "PROPERTY",
            "vehicle-purchase-invoice-png-ocr/1.0.0" or
            "vehicle-purchase-invoice-jpeg-ocr/1.0.0" or
            "vehicle-purchase-invoice-pdf-ocr/1.0.0" => "VEHICLE",
            _ => customerType,
        };
    }

    public static string ForCustomer(string ownerType) =>
        ownerType switch
        {
            "PROPERTY" or "VEHICLE" or "BANK_ACCOUNT" => "ASSET",
            "INSURANCE_POLICY" => "INSURANCE",
            "TAX_RECORD" => "TAX",
            _ => ownerType,
        };
    public static bool CanReviewAssetType(string customerType, string? extractionVersion) =>
        customerType == "ASSET" && extractionVersion is
            "fixture-extractor/1.0.0" or "financial-csv/1.0.0" or "financial-xlsx/1.0.0";

    public static string? CustomerAssetType(string ownerType) =>
        ownerType is "PROPERTY" or "VEHICLE" or "BANK_ACCOUNT" ? ownerType : null;

    public static IReadOnlyDictionary<string, string> ReviewedTypes(
        IReadOnlyList<(string SourceFactId, string CustomerType, string? ExtractionVersion)> selected,
        IReadOnlyList<(string SourceFactId, string AssetType)>? choices)
    {
        const string invalid = "FINANCIAL_REVIEW_SELECTION_INVALID";
        if (selected.Count is < 1 or > 50 || choices?.Count > 50)
            throw new FinancialSourceSelectionException(invalid);
        var source = new Dictionary<string, (string Type, string? Version)>(StringComparer.Ordinal);
        foreach (var fact in selected)
            if (string.IsNullOrWhiteSpace(fact.SourceFactId) || !source.TryAdd(fact.SourceFactId, (fact.CustomerType, fact.ExtractionVersion)))
                throw new FinancialSourceSelectionException(invalid);
        var assigned = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var choice in choices ?? [])
        {
            if (string.IsNullOrWhiteSpace(choice.SourceFactId) || choice.AssetType is not ("PROPERTY" or "VEHICLE" or "BANK_ACCOUNT") ||
                !source.TryGetValue(choice.SourceFactId, out var fact) || !CanReviewAssetType(fact.Type, fact.Version) ||
                !assigned.TryAdd(choice.SourceFactId, choice.AssetType))
                throw new FinancialSourceSelectionException(invalid);
        }
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (id, fact) in source)
        {
            var ownerType = ForOwner(fact.Type, fact.Version);
            if (CanReviewAssetType(fact.Type, fact.Version))
            {
                if (!assigned.TryGetValue(id, out ownerType))
                    throw new FinancialSourceSelectionException("FINANCIAL_REVIEW_ASSET_TYPE_REQUIRED");
            }
            else if (ownerType == "ASSET")
                throw new FinancialSourceSelectionException("FINANCIAL_REVIEW_ASSET_SOURCE_UNSUPPORTED");
            result.Add(id, ownerType);
        }
        return result;
    }
}

public sealed class FinancialSourceSelectionException(string code) : Exception("Reviewed source types are invalid.")
{
    public string Code { get; } = code;
}
