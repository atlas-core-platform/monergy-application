namespace Monergy.Contracts;
public sealed record AssetRecordSnapshot(DateOnly AsOfDate, string Currency,
    InvestmentRecordList Investments, PropertyRecordList Properties, VehicleRecordList Vehicles);
