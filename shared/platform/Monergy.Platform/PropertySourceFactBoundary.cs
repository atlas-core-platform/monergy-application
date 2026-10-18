namespace Monergy.Platform;

// Customer source categories are presentation labels; owner contracts retain canonical object types.
public static class PropertySourceFactBoundary
{
    public static string ForOwner(string customerType, string? extractionVersion) =>
        customerType == "ASSET" && extractionVersion is
            "property-sale-deed-png-ocr/1.0.0" or
            "property-sale-deed-jpeg-ocr/1.0.0" or
            "property-sale-deed-pdf-ocr/1.0.0" or
            "property-registration-png-ocr/1.0.0" or
            "property-registration-jpeg-ocr/1.0.0" or
            "property-registration-pdf-ocr/1.0.0"
            ? "PROPERTY"
            : customerType;

    public static string ForCustomer(string ownerType) =>
        ownerType == "PROPERTY" ? "ASSET" : ownerType;
}
