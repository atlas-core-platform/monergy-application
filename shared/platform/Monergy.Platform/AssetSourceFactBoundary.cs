namespace Monergy.Platform;

// Customer source categories are presentation labels; owner contracts retain canonical object types.
public static class AssetSourceFactBoundary
{
    public static string ForOwner(string customerType, string? extractionVersion)
    {
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
        ownerType is "PROPERTY" or "VEHICLE" ? "ASSET" : ownerType;
}
