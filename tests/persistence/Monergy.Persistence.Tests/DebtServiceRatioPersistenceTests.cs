using Dapper;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.FinancialRules.Infrastructure;
using Npgsql;
using Xunit;

namespace Monergy.Persistence.Tests;

public sealed partial class PersistentFoundationTests
{
    [Fact]
    public async Task DebtServiceRatioReceiptIsPhysicallyInsertedSurvivesAdapterRestartIsTenantBoundAndCannotBeRewritten()
    {
        var rules = Configuration("FinancialRules");
        var customer = "debt-service-" + Guid.NewGuid().ToString("N");
        var income = new MonthlyCashflowLineDraft(Guid.NewGuid().ToString("N"), "INCOME", "SALARY", "Salary", "100000.00", "INR", new(2026, 10, 31), "Customer reviewed salary");
        var debt = new MonthlyCashflowLineDraft(Guid.NewGuid().ToString("N"), "EXPENSE", "LOAN", "Loan EMI", "10000.00", "INR", new(2026, 10, 31), "Customer reviewed debt payment");
        var position = new ReviewedMonthlyCashflowPosition("position-" + customer, Guid.NewGuid().ToString("N"), customer,
            new(new(2026, 10, 31), "INR", [income, debt], true, true),
            [new(income, null), new(debt, null)], "actor-debt-service", DateTimeOffset.UtcNow);
        var cashflow = new MonthlyCashflowCalculation("cashflow-" + customer, Guid.NewGuid().ToString("N"), customer, position,
            MonthlyCashflowDefinition.Id, MonthlyCashflowDefinition.Version, MonthlyCashflowDefinition.DefinitionHash, MonthlyCashflowDefinition.Semantics,
            "CALCULATED", "100000.00", "10000.00", "90000.00", [], "actor-debt-service", DateTimeOffset.UtcNow);
        var value = new DebtServiceRatioCalculation("debt-ratio-" + customer, Guid.NewGuid().ToString("N"), customer, cashflow, [debt.LineId], true,
            DebtServiceRatioDefinition.Id, DebtServiceRatioDefinition.Version, DebtServiceRatioDefinition.DefinitionHash, DebtServiceRatioDefinition.Semantics,
            "CALCULATED", "100000.00", "10000.00", "10.00", [], "actor-debt-service", DateTimeOffset.UtcNow);
        const string fingerprint = "p2-04-physical-receipt";

        await using (var repository = new PostgresDebtServiceRatioCalculations(rules))
        {
            Assert.True(MonthlyCashflowData.Same(value, await repository.CreateAsync("T001", customer, value.RecordedBy, fingerprint, value, default)));
            Assert.True(MonthlyCashflowData.Same(value, await repository.CreateAsync("T001", customer, value.RecordedBy, fingerprint, value, default)));
        }

        await using (var repository = new PostgresDebtServiceRatioCalculations(rules))
        {
            Assert.True(MonthlyCashflowData.Same(value, await repository.ReadAsync("T001", customer, value.CalculationId, default)));
            Assert.Single((await repository.ListAsync("T001", customer, null, default)).Items);
            Assert.Null(await repository.ReadAsync("T002", customer, value.CalculationId, default));
            Assert.Null(await repository.ReadAsync("T001", "foreign", value.CalculationId, default));
            Assert.Equal(409, (await Assert.ThrowsAsync<TenantBoundaryException>(() => repository.CreateAsync("T001", customer, value.RecordedBy, "changed", value, default))).Status);
        }

        await using var sql = new NpgsqlConnection(Runtime("financial-rules"));
        var physical = await sql.QuerySingleAsync<PhysicalReceipt>(
            "SELECT tenant_id AS TenantId,customer_id AS CustomerId,calculation_id AS CalculationId,payload->>'state' AS State,payload->>'ratioPercent' AS RatioPercent FROM financial_rules.debt_service_ratio_results WHERE calculation_id=@Id;",
            new { Id = value.CalculationId });
        Assert.Equal("T001", physical.TenantId);
        Assert.Equal(customer, physical.CustomerId);
        Assert.Equal(value.CalculationId, physical.CalculationId);
        Assert.Equal("CALCULATED", physical.State);
        Assert.Equal("10.00", physical.RatioPercent);

        var update = await Assert.ThrowsAsync<PostgresException>(() => sql.ExecuteAsync(
            "UPDATE financial_rules.debt_service_ratio_results SET payload=payload WHERE calculation_id=@Id;", new { Id = value.CalculationId }));
        Assert.Equal("42501", update.SqlState);
        var delete = await Assert.ThrowsAsync<PostgresException>(() => sql.ExecuteAsync(
            "DELETE FROM financial_rules.debt_service_ratio_results WHERE calculation_id=@Id;", new { Id = value.CalculationId }));
        Assert.Equal("42501", delete.SqlState);
        var truncate = await Assert.ThrowsAsync<PostgresException>(() => sql.ExecuteAsync(
            "TRUNCATE financial_rules.debt_service_ratio_results;"));
        Assert.Equal("42501", truncate.SqlState);
    }

    private sealed record PhysicalReceipt(string TenantId, string CustomerId, string CalculationId, string State, string RatioPercent);
}
