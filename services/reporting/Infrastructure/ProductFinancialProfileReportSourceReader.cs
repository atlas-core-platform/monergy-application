using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.Reporting.Application;
using Monergy.Services.Reporting.Domain;

namespace Monergy.Services.Reporting.Infrastructure;

// LOCAL/UAT product adapter. Reporting remains the report owner, while Financial Profile
// remains the authority for every included financial fact and provenance reference.
public sealed class ProductFinancialProfileReportSourceReader(
    IHttpClientFactory clients,
    IHttpContextAccessor accessor,
    IConfiguration configuration) : IReportSourceReader
{
    public const string FinancialProfileClient = "product-reporting-financial-profile";
    public const string FinancialRulesClient = "product-reporting-financial-rules";
    private static readonly HashSet<string> SupportedFactTypes = new(StringComparer.Ordinal)
    { "INCOME", "EXPENSE", "LOAN", "ASSET", "INVESTMENT", "INSURANCE", "TAX" };
    private readonly Uri endpoint = TenantAccessIntegration.LoopbackRoot(
        TenantAccessIntegration.Required(configuration, "Monergy:ProductReporting:FinancialProfileUrl"));
    private readonly Uri rulesEndpoint = TenantAccessIntegration.LoopbackRoot(
        TenantAccessIntegration.Required(configuration, "Monergy:ProductReporting:FinancialRulesUrl"));

    public static bool Selected(IConfiguration configuration) =>
        configuration.GetValue<bool>("Monergy:ProductReporting:Enabled");

    public async Task<ReportSourceSnapshot?> ReadAsync(
        ReportSourceReadContext context,
        CancellationToken cancellationToken)
    {
        var proof = accessor.HttpContext?.Features.Get<TenantBoundaryProof>();
        if (proof is null ||
            proof.ContractName != ReportingContractNames.GenerateReport ||
            proof.CapabilityId != "reporting.report.generate" ||
            proof.CustomerId != context.CustomerId ||
            proof.ActorId != context.Security.Actor.ActorId ||
            proof.SessionId != context.Security.Actor.AuthenticationContextId)
        {
            throw Failure("report.source.boundary-invalid", ContractErrorCategory.AccessDenied,
                "Current reporting authorization is required.", context.CorrelationId);
        }

        var sourceSecurity = context.Security with
        {
            Workload = new WorkloadContext("reporting", "local-uat-reporting:" + proof.TenantId),
        };
        var profileId = "financial-profile-" + context.CustomerId;
        var profileRequest = Request(
            ReportingSourceRequestId("profile"),
            context,
            sourceSecurity,
            Vs02ContractNames.GetFinancialProfile,
            new GetFinancialProfile(profileId, context.CustomerId));
        var profileResult = await SendAsync<GetFinancialProfile, FinancialProfileDetails>(
            proof, "contracts/cid-030/v1", profileRequest, cancellationToken).ConfigureAwait(false);

        FinancialProfileDetails? profile = null;
        if (profileResult.Outcome == ContractOutcome.Success && profileResult.Data is not null)
            profile = profileResult.Data;
        else if (profileResult.Outcome != ContractOutcome.Rejected ||
                 profileResult.Error?.Category != ContractErrorCategory.NotFound)
            throw OwnerFailure(profileResult.Error, context.CorrelationId);

        if (profile is not null &&
            (profile.CustomerId != context.CustomerId || profile.FinancialProfileId != profileId ||
            profile.Facts.Any(fact =>
                fact.CustomerId != context.CustomerId ||
                fact.FinancialProfileId != profileId ||
                string.IsNullOrWhiteSpace(fact.FinancialFactId) ||
                string.IsNullOrWhiteSpace(fact.FinancialProvenanceId)) ||
            profile.Facts.Select(fact => fact.FinancialFactId).Distinct(StringComparer.Ordinal).Count() !=
                profile.Facts.Count))
        {
            throw Failure("report.source.profile-boundary-invalid", ContractErrorCategory.AccessDenied,
                "The financial profile response is outside the current customer boundary.",
                context.CorrelationId);
        }

        var supported = (profile?.Facts ?? [])
            .Select(fact => fact with { FactType = FinancialSourceFactBoundary.ForCustomer(fact.FactType) })
            .Where(fact => SupportedFactTypes.Contains(fact.FactType))
            .OrderBy(fact => fact.FactType, StringComparer.Ordinal)
            .ThenBy(fact => fact.Label, StringComparer.Ordinal)
            .ToArray();

        var items = ImmutableArray.CreateBuilder<ReportLineItem>(supported.Length);
        foreach (var fact in supported)
        {
            var provenanceRequest = Request(
                ReportingSourceRequestId("provenance"),
                context,
                sourceSecurity,
                Vs02ContractNames.GetFinancialProvenance,
                new GetFinancialProvenance(fact.FinancialProvenanceId, context.CustomerId));
            var provenanceResult = await SendAsync<GetFinancialProvenance, FinancialProvenance>(
                proof, "contracts/cid-033/v1", provenanceRequest, cancellationToken).ConfigureAwait(false);
            if (provenanceResult.Outcome != ContractOutcome.Success || provenanceResult.Data is null)
                throw OwnerFailure(provenanceResult.Error, context.CorrelationId);

            var provenance = provenanceResult.Data;
            if (provenance.CustomerId != context.CustomerId ||
                provenance.FinancialFactId != fact.FinancialFactId ||
                provenance.FinancialProvenanceId != fact.FinancialProvenanceId ||
                string.IsNullOrWhiteSpace(provenance.EvidenceId))
            {
                throw Failure("report.source.provenance-boundary-invalid", ContractErrorCategory.AccessDenied,
                    "Financial provenance is outside the current customer boundary.",
                    context.CorrelationId);
            }

            items.Add(new ReportLineItem(
                fact.Label,
                fact.Value,
                fact.Currency,
                new ReportSourceReference(
                    "FinancialFact",
                    fact.FinancialFactId,
                    ReportingAuthority.FinancialProfileService,
                    provenance.EvidenceId,
                    provenance.FinancialProvenanceId,
                    null)));
        }

        if (profile is not null)
        {
            foreach (var item in await ReadTaxRecordsAsync(
                proof, context, sourceSecurity, profileId, cancellationToken).ConfigureAwait(false))
                items.Add(item);
        }

        var goals = await ReadFinancialGoalsAsync(
            proof, context, cancellationToken).ConfigureAwait(false);
        foreach (var item in goals.Items) items.Add(item);

        // Preserve the former no-profile behavior unless the Financial Profile owner supplies
        // a complete, current Customer-defined goal corpus for the new bounded report source.
        if (profile is null && goals.LatestRecordedAt is null) return null;

        if (context.FinancialReview is { } review)
        {
            foreach (var item in await ReadFinancialReviewAsync(
                proof, context, review, cancellationToken).ConfigureAwait(false))
                items.Add(item);
        }

        if (items.Count == 0) return null;
        if (items.Count > 100)
            throw Failure("report.source.corpus-limit", ContractErrorCategory.DependencyFailure,
                "The complete supported report source exceeds the current report limit.",
                context.CorrelationId, true);

        var asOf = profile?.UpdatedAt ?? goals.LatestRecordedAt!.Value;
        if (goals.LatestRecordedAt > asOf) asOf = goals.LatestRecordedAt.Value;

        return new ReportSourceSnapshot(
            context.CustomerId,
            asOf,
            items.ToImmutable(),
            null);
    }

    private async Task<(IReadOnlyList<ReportLineItem> Items, DateTimeOffset? LatestRecordedAt)>
        ReadFinancialGoalsAsync(
            TenantBoundaryProof proof,
            ReportSourceReadContext context,
            CancellationToken cancellationToken)
    {
        var root = "local/v1/customers/" + Uri.EscapeDataString(context.CustomerId) +
            "/financial-goals";
        var list = await GetOwnerAsync<FinancialGoalList>(proof, root,
            context.CorrelationId, cancellationToken).ConfigureAwait(false);
        if (list.Items is null || list.Items.Count > 200 || list.HasMore ||
            list.Items.Any(item => item is null) ||
            list.Items.Select(item => item?.FinancialGoalId).Distinct(StringComparer.Ordinal).Count() !=
                list.Items.Count)
        {
            throw Failure("report.source.financial-goal-corpus-incomplete",
                ContractErrorCategory.DependencyFailure,
                "The Customer-defined financial-goal source is not a complete supported corpus.",
                context.CorrelationId, true);
        }

        var items = new List<ReportLineItem>(list.Items.Count);
        DateTimeOffset? latest = null;
        foreach (var listed in list.Items.OrderBy(item => item.Goal.Name, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.FinancialGoalId, StringComparer.Ordinal))
        {
            if (!ValidFinancialGoal(listed, context.CustomerId))
                throw FinancialGoalBoundary(context.CorrelationId);
            var exact = await GetOwnerAsync<CustomerFinancialGoal>(proof,
                root + "/" + Uri.EscapeDataString(listed.FinancialGoalId) +
                "?revision=" + listed.Revision.ToString(CultureInfo.InvariantCulture),
                context.CorrelationId, cancellationToken).ConfigureAwait(false);
            if (!ValidFinancialGoal(exact, context.CustomerId) || exact != listed)
                throw FinancialGoalBoundary(context.CorrelationId);

            items.Add(new ReportLineItem(
                exact.Goal.Name + " — customer-defined target",
                exact.Goal.TargetAmount,
                exact.Goal.Currency,
                new ReportSourceReference(
                    SourceType: "FinancialGoal",
                    SourceId: exact.FinancialGoalId,
                    AuthoritativeOwner: ReportingAuthority.FinancialProfileService,
                    EvidenceReferenceId: null,
                    FinancialProvenanceReferenceId: null,
                    CalculationLineageReferenceId: null,
                    FinancialGoal: new FinancialGoalReportSnapshot(
                        exact.Revision,
                        exact.OperationId,
                        exact.Goal.TargetDate,
                        exact.LifecycleStatus,
                        exact.LifecycleAsOfDate,
                        exact.PriorityLevel,
                        exact.PriorityAsOfDate,
                        exact.RecordedAt))));
            if (latest is null || exact.RecordedAt > latest) latest = exact.RecordedAt;
        }
        return (items, latest);
    }

    private async Task<IReadOnlyList<ReportLineItem>> ReadTaxRecordsAsync(
        TenantBoundaryProof proof,
        ReportSourceReadContext context,
        TrustedSecurityContext sourceSecurity,
        string profileId,
        CancellationToken cancellationToken)
    {
        var list = await GetOwnerAsync<TaxRecordList>(proof,
            "local/v1/customers/" + Uri.EscapeDataString(context.CustomerId) + "/tax-records",
            context.CorrelationId, cancellationToken).ConfigureAwait(false);
        if (list.Items is null || list.Items.Count > 200 || list.HasMore ||
            list.Items.Select(item => item?.TaxRecordId).Distinct(StringComparer.Ordinal).Count() !=
                list.Items.Count)
        {
            throw Failure("report.source.tax-record-corpus-incomplete",
                ContractErrorCategory.DependencyFailure,
                "The reviewed tax-record source is not a complete supported corpus.",
                context.CorrelationId, true);
        }

        var items = new List<ReportLineItem>(list.Items.Count);
        foreach (var record in list.Items.OrderBy(item => item.Tax.Name, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.TaxRecordId, StringComparer.Ordinal))
        {
            if (!ValidTaxRecord(record, context.CustomerId))
                throw TaxRecordBoundary(context.CorrelationId);

            var factRequest = Request(
                ReportingSourceRequestId("tax-record-source"),
                context,
                sourceSecurity,
                Vs02ContractNames.GetFinancialFact,
                new GetFinancialFact(record.Tax.SourceFinancialFactId, context.CustomerId,
                    record.Tax.SourceRevision));
            var factResult = await SendAsync<GetFinancialFact, AuthoritativeFinancialFact>(
                proof, "contracts/cid-032/v1", factRequest, cancellationToken).ConfigureAwait(false);
            if (factResult.Outcome != ContractOutcome.Success || factResult.Data is null)
                throw OwnerFailure(factResult.Error, context.CorrelationId);
            var fact = factResult.Data;
            if (fact.CustomerId != context.CustomerId || fact.FinancialProfileId != profileId ||
                fact.FinancialFactId != record.Tax.SourceFinancialFactId ||
                fact.Revision != record.Tax.SourceRevision ||
                FinancialSourceFactBoundary.ForCustomer(fact.FactType) != "TAX" ||
                fact.Currency != record.Currency ||
                fact.FinancialProvenanceId != record.SourceProvenanceId)
                throw TaxRecordBoundary(context.CorrelationId);

            var provenanceRequest = Request(
                ReportingSourceRequestId("tax-record-provenance"),
                context,
                sourceSecurity,
                Vs02ContractNames.GetFinancialProvenance,
                new GetFinancialProvenance(record.SourceProvenanceId, context.CustomerId));
            var provenanceResult = await SendAsync<GetFinancialProvenance, FinancialProvenance>(
                proof, "contracts/cid-033/v1", provenanceRequest, cancellationToken).ConfigureAwait(false);
            if (provenanceResult.Outcome != ContractOutcome.Success || provenanceResult.Data is null)
                throw OwnerFailure(provenanceResult.Error, context.CorrelationId);
            var provenance = provenanceResult.Data;
            if (provenance.CustomerId != context.CustomerId ||
                provenance.FinancialFactId != fact.FinancialFactId ||
                provenance.FinancialProvenanceId != record.SourceProvenanceId ||
                provenance.EvidenceId != record.EvidenceId ||
                provenance.DocumentVersionId != record.DocumentVersionId)
                throw TaxRecordBoundary(context.CorrelationId);

            items.Add(new ReportLineItem(
                record.Tax.Name + " — " + TaxAmountKind(record.Tax.AmountKind),
                record.Tax.Amount,
                record.Currency,
                new ReportSourceReference(
                    SourceType: "TaxRecord",
                    SourceId: record.TaxRecordId,
                    AuthoritativeOwner: ReportingAuthority.FinancialProfileService,
                    EvidenceReferenceId: record.EvidenceId,
                    FinancialProvenanceReferenceId: record.SourceProvenanceId,
                    CalculationLineageReferenceId: null,
                    FinancialFactReferenceId: record.Tax.SourceFinancialFactId,
                    FinancialFactRevision: record.Tax.SourceRevision,
                    DocumentVersionReferenceId: record.DocumentVersionId)));
        }

        return items;
    }

    private async Task<T> GetOwnerAsync<T>(
        TenantBoundaryProof proof,
        string path,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var client = clients.CreateClient(FinancialProfileClient);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoint, path));
        request.Headers.Add("X-Monergy-Tenant", proof.TenantId);
        request.Headers.Add("X-Monergy-Session", proof.SessionId);
        try
        {
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw Failure("report.source.financial-profile-denied",
                    ContractErrorCategory.AccessDenied,
                    "Current financial profile authorization is required.", correlationId);
            if (!response.IsSuccessStatusCode)
                throw Failure("report.source.financial-profile-unavailable",
                    ContractErrorCategory.DependencyFailure,
                    "The financial profile source is unavailable.", correlationId, true);
            return await response.Content.ReadFromJsonAsync<T>(ContractJson.Options, cancellationToken)
                .ConfigureAwait(false) ?? throw Failure("report.source.response-malformed",
                    ContractErrorCategory.DependencyFailure,
                    "The financial profile response is invalid.", correlationId);
        }
        catch (JsonException)
        {
            throw Failure("report.source.response-malformed", ContractErrorCategory.DependencyFailure,
                "The financial profile response is invalid.", correlationId);
        }
        catch (HttpRequestException)
        {
            throw Failure("report.source.financial-profile-unavailable",
                ContractErrorCategory.DependencyFailure,
                "The financial profile source is unavailable.", correlationId, true);
        }
    }

    private static bool ValidTaxRecord(ReviewedTaxRecord? record, string customerId) =>
        record is { Tax: not null } && record.CustomerId == customerId &&
        TenantAccessClient.Identifier(record.TaxRecordId, 80) &&
        Guid.TryParseExact(record.OperationId, "N", out _) && record.Revision > 0 &&
        record.VerificationStatus == "CUSTOMER_REVIEWED" &&
        Text(record.Tax.Name) && Text(record.Tax.Jurisdiction) && Text(record.Tax.TaxType) &&
        Text(record.Tax.TaxYearLabel, 30) && record.Tax.Amount is >= 0 and <= 999999999999.99m &&
        decimal.Round(record.Tax.Amount, 2) == record.Tax.Amount &&
        record.Tax.AmountKind is "PAID" or "DUE" or "WITHHELD" &&
        record.Tax.PeriodStart.Year >= 1900 && record.Tax.PeriodEnd >= record.Tax.PeriodStart &&
        TenantAccessClient.Identifier(record.Tax.SourceFinancialFactId, 160) &&
        record.Tax.SourceRevision > 0 && record.Currency.Length == 3 &&
        record.Currency.All(char.IsAsciiLetterUpper) &&
        TenantAccessClient.Identifier(record.SourceProvenanceId, 160) &&
        TenantAccessClient.Identifier(record.DocumentVersionId, 160) &&
        TenantAccessClient.Identifier(record.EvidenceId, 160) &&
        TenantAccessClient.Identifier(record.RecordedBy, 160) && record.RecordedAt != default;

    private static bool ValidFinancialGoal(CustomerFinancialGoal? record, string customerId) =>
        record is { Goal: not null } && record.CustomerId == customerId &&
        TenantAccessClient.Identifier(record.FinancialGoalId, 80) &&
        Guid.TryParseExact(record.OperationId, "N", out _) &&
        !record.OperationId.Any(char.IsAsciiLetterUpper) && record.Revision > 0 &&
        record.DeclarationStatus == "CUSTOMER_DEFINED" &&
        Text(record.Goal.Name) && record.Goal.Name == record.Goal.Name.Trim() &&
        record.Goal.TargetAmount is > 0 and <= 999999999999.99m &&
        decimal.Round(record.Goal.TargetAmount, 2) == record.Goal.TargetAmount &&
        record.Goal.Currency is { Length: 3 } && record.Goal.Currency.All(char.IsAsciiLetterUpper) &&
        record.Goal.TargetDate.Year is >= 1900 and <= 2199 &&
        record.LifecycleBasis == "CUSTOMER_REPORTED" &&
        (record.LifecycleStatus == "IN_PROGRESS" &&
             (record.LifecycleAsOfDate is null || record.LifecycleAsOfDate.Value.Year >= 1900) ||
         record.LifecycleStatus is "PAUSED" or "REPORTED_ACHIEVED" or "STOPPED" &&
             record.LifecycleAsOfDate is { Year: >= 1900 }) &&
        record.PriorityBasis == "CUSTOMER_REPORTED" &&
        record.PriorityLevel is "NOT_SET" or "LOW" or "MEDIUM" or "HIGH" &&
        (record.PriorityLevel == "NOT_SET" && record.PriorityAsOfDate is null ||
         record.PriorityAsOfDate is { Year: >= 1900 }) &&
        TenantAccessClient.Identifier(record.RecordedBy, 160) && record.RecordedAt != default;

    private static bool Text(string? value, int maximum = 120) =>
        !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= maximum &&
        !value.Any(char.IsControl);

    private static string TaxAmountKind(string value) => value switch
    {
        "PAID" => "tax paid",
        "DUE" => "tax due",
        "WITHHELD" => "tax withheld",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static ReportSourceException TaxRecordBoundary(string correlationId) =>
        Failure("report.source.tax-record-boundary-invalid", ContractErrorCategory.AccessDenied,
            "A reviewed tax record is outside the current customer boundary.", correlationId);

    private static ReportSourceException FinancialGoalBoundary(string correlationId) =>
        Failure("report.source.financial-goal-boundary-invalid", ContractErrorCategory.AccessDenied,
            "A Customer-defined financial goal is outside the current customer boundary.", correlationId);

    private async Task<IReadOnlyList<ReportLineItem>> ReadFinancialReviewAsync(
        TenantBoundaryProof proof,
        ReportSourceReadContext context,
        FinancialReviewReceiptSelection selection,
        CancellationToken cancellationToken)
    {
        var customerPath = "local/v1/customers/" + Uri.EscapeDataString(context.CustomerId) + "/";
        var allocation = await ReadRuleAsync<AssetAllocationCalculation>(proof,
            customerPath + "net-worth/asset-allocations/" + Uri.EscapeDataString(selection.AssetAllocationCalculationId),
            context.CorrelationId, cancellationToken).ConfigureAwait(false);
        var cashflowRate = await ReadRuleAsync<MonthlyNetCashflowRateCalculation>(proof,
            customerPath + "monthly-cashflow/net-cashflow-rates/" + Uri.EscapeDataString(selection.MonthlyNetCashflowRateCalculationId),
            context.CorrelationId, cancellationToken).ConfigureAwait(false);
        var emiShare = await ReadRuleAsync<MonthlyEmiShareCalculation>(proof,
            customerPath + "monthly-cashflow/emi-shares/" + Uri.EscapeDataString(selection.MonthlyEmiShareCalculationId),
            context.CorrelationId, cancellationToken).ConfigureAwait(false);
        var coverage = await ReadRuleAsync<MonthlyDebtServiceCoverageCalculation>(proof,
            customerPath + "monthly-cashflow/debt-service-coverages/" + Uri.EscapeDataString(selection.MonthlyDebtServiceCoverageCalculationId),
            context.CorrelationId, cancellationToken).ConfigureAwait(false);

        if (!AssetAllocationData.Calculation(allocation, context.CustomerId))
            throw InconsistentReview(context.CorrelationId, "The asset-allocation receipt is not canonical.");
        if (!MonthlyNetCashflowRateData.Calculation(cashflowRate, context.CustomerId))
            throw InconsistentReview(context.CorrelationId, "The monthly net-cash-flow receipt is not canonical.");
        if (!MonthlyEmiShareData.Calculation(emiShare, context.CustomerId))
            throw InconsistentReview(context.CorrelationId, "The monthly EMI-share receipt is not canonical.");
        if (!MonthlyDebtServiceCoverageData.Calculation(coverage, context.CustomerId))
            throw InconsistentReview(context.CorrelationId, "The monthly debt-service-coverage receipt is not canonical.");
        if (allocation.CalculationId != selection.AssetAllocationCalculationId ||
            cashflowRate.CalculationId != selection.MonthlyNetCashflowRateCalculationId ||
            emiShare.CalculationId != selection.MonthlyEmiShareCalculationId ||
            coverage.CalculationId != selection.MonthlyDebtServiceCoverageCalculationId)
            throw InconsistentReview(context.CorrelationId, "Financial Rules returned a different receipt identity.");
        if (allocation.State != "CALCULATED" || cashflowRate.State != "CALCULATED" ||
            emiShare.State != "CALCULATED" || coverage.State != "CALCULATED")
            throw InconsistentReview(context.CorrelationId, "Every selected receipt must be calculated.");
        if (
            cashflowRate.SourceMonthlyCashflow.CalculationId != emiShare.SourceMonthlyCashflow.CalculationId ||
            cashflowRate.SourceMonthlyCashflow.CalculationId != coverage.SourceMonthlyCashflow.CalculationId ||
            coverage.SourceMonthlyEmiShare.CalculationId != emiShare.CalculationId ||
            !MonthlyCashflowData.Same(coverage.SourceMonthlyEmiShare, emiShare))
            throw InconsistentReview(context.CorrelationId, "The selected monthly receipts do not share exact sources.");
        if (
            allocation.SourceNetWorth.Position.Position.AsOfDate !=
                cashflowRate.SourceMonthlyCashflow.Position.Position.AsOfDate ||
            allocation.SourceNetWorth.Position.Position.Currency !=
                cashflowRate.SourceMonthlyCashflow.Position.Position.Currency)
            throw InconsistentReview(context.CorrelationId, "Net worth and monthly receipts must share a date and currency.");
        if (allocation.RecordedBy != context.Security.Actor.ActorId ||
            cashflowRate.RecordedBy != context.Security.Actor.ActorId ||
            emiShare.RecordedBy != context.Security.Actor.ActorId ||
            coverage.RecordedBy != context.Security.Actor.ActorId)
            throw InconsistentReview(context.CorrelationId, "The selected receipts were not recorded by the current actor.");

        var items = allocation.Categories.Select(category => CalculationItem(
                "Asset allocation — " + category.Category.Replace('_', ' ').ToLowerInvariant(),
                category.Percentage, "%", allocation.CalculationId, allocation.RuleId,
                allocation.RuleVersion, allocation.DefinitionHash, allocation.NumericSemantics,
                [allocation.SourceNetWorth.CalculationId]))
            .Append(CalculationItem("Monthly net cash-flow rate", cashflowRate.RatePercent!, "%",
                cashflowRate.CalculationId, cashflowRate.RuleId, cashflowRate.RuleVersion,
                cashflowRate.DefinitionHash, cashflowRate.NumericSemantics,
                [cashflowRate.SourceMonthlyCashflow.CalculationId]))
            .Append(CalculationItem("Monthly EMI share", emiShare.RatePercent!, "%",
                emiShare.CalculationId, emiShare.RuleId, emiShare.RuleVersion,
                emiShare.DefinitionHash, emiShare.NumericSemantics,
                [emiShare.SourceMonthlyCashflow.CalculationId, .. emiShare.SourceLoans.Select(loan => loan.LoanRecordId)]))
            .Append(CalculationItem("Monthly debt-service coverage", coverage.CoverageMultiple!, "x",
                coverage.CalculationId, coverage.RuleId, coverage.RuleVersion,
                coverage.DefinitionHash, coverage.NumericSemantics,
                [coverage.SourceMonthlyCashflow.CalculationId, coverage.SourceMonthlyEmiShare.CalculationId]))
            .ToArray();
        return items;
    }

    private static ReportSourceException InconsistentReview(string correlationId, string message) =>
        Failure("report.source.financial-review-inconsistent", ContractErrorCategory.AccessDenied,
            message, correlationId);

    private static ReportLineItem CalculationItem(string label, string value, string unit,
        string calculationId, string ruleId, string ruleVersion, string definitionHash,
        string numericSemantics, IReadOnlyList<string> sourceCalculationIds) => new(
        label,
        decimal.Parse(value, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture),
        unit,
        new ReportSourceReference("FinancialCalculation", calculationId,
            ReportingAuthority.FinancialRulesService, null, null, calculationId,
            ruleId, ruleVersion, definitionHash, numericSemantics, sourceCalculationIds));

    private async Task<T> ReadRuleAsync<T>(TenantBoundaryProof proof, string path,
        string correlationId, CancellationToken cancellationToken)
    {
        var client = clients.CreateClient(FinancialRulesClient);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(rulesEndpoint, path));
        request.Headers.Add("X-Monergy-Tenant", proof.TenantId);
        request.Headers.Add("X-Monergy-Session", proof.SessionId);
        try
        {
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
                throw Failure("report.source.financial-review-denied", ContractErrorCategory.AccessDenied,
                    "A selected Financial Rules receipt is unavailable under the current access.", correlationId);
            if (!response.IsSuccessStatusCode)
                throw Failure("report.source.financial-rules-unavailable", ContractErrorCategory.DependencyFailure,
                    "The Financial Rules source is unavailable.", correlationId, true);
            return await response.Content.ReadFromJsonAsync<T>(ContractJson.Options, cancellationToken)
                .ConfigureAwait(false) ?? throw Failure("report.source.response-malformed",
                    ContractErrorCategory.DependencyFailure, "A Financial Rules response is invalid.", correlationId);
        }
        catch (JsonException)
        {
            throw Failure("report.source.response-malformed", ContractErrorCategory.DependencyFailure,
                "A Financial Rules response is invalid.", correlationId);
        }
        catch (HttpRequestException)
        {
            throw Failure("report.source.financial-rules-unavailable", ContractErrorCategory.DependencyFailure,
                "The Financial Rules source is unavailable.", correlationId, true);
        }
    }

    private static ContractRequest<TPayload> Request<TPayload>(
        string requestId,
        ReportSourceReadContext context,
        TrustedSecurityContext security,
        string contractName,
        TPayload payload) => new(
            contractName,
            ContractGuard.CurrentVersion,
            requestId,
            context.CorrelationId,
            context.CausationId,
            security,
            null,
            payload);

    private async Task<ContractResult<TData>> SendAsync<TPayload, TData>(
        TenantBoundaryProof proof,
        string path,
        ContractRequest<TPayload> body,
        CancellationToken cancellationToken)
    {
        var client = clients.CreateClient(FinancialProfileClient);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, path))
        {
            Content = JsonContent.Create(body, options: ContractJson.Options),
        };
        request.Headers.Add("X-Monergy-Tenant", proof.TenantId);
        request.Headers.Add("X-Monergy-Session", proof.SessionId);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            throw Failure("report.source.financial-profile-unavailable",
                ContractErrorCategory.DependencyFailure,
                "The financial profile source is unavailable.", body.CorrelationId, true);
        }
        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw Failure("report.source.financial-profile-denied",
                    ContractErrorCategory.AccessDenied,
                    "Current financial profile authorization is required.", body.CorrelationId);
            if ((int)response.StatusCode >= 500 || (int)response.StatusCode is >= 300 and < 400)
                throw Failure("report.source.financial-profile-unavailable",
                    ContractErrorCategory.DependencyFailure,
                    "The financial profile source is unavailable.", body.CorrelationId, true);

            try
            {
                var result = await response.Content.ReadFromJsonAsync<ContractResult<TData>>(
                    ContractJson.Options, cancellationToken).ConfigureAwait(false)
                    ?? throw Failure("report.source.response-malformed",
                        ContractErrorCategory.DependencyFailure,
                        "The financial profile response is invalid.", body.CorrelationId);
                if (result.ContractName != body.ContractName ||
                    result.ContractVersion != body.ContractVersion ||
                    result.RequestId != body.RequestId ||
                    result.CorrelationId != body.CorrelationId)
                    throw Failure("report.source.response-malformed",
                        ContractErrorCategory.DependencyFailure,
                        "The financial profile response is invalid.", body.CorrelationId);
                return result;
            }
            catch (JsonException)
            {
                throw Failure("report.source.response-malformed",
                    ContractErrorCategory.DependencyFailure,
                    "The financial profile response is invalid.", body.CorrelationId);
            }
        }
    }

    private static string ReportingSourceRequestId(string kind) =>
        "report-source-" + kind + "-" + Guid.NewGuid().ToString("N");

    private static ReportSourceException OwnerFailure(ContractError? error, string correlationId) =>
        error is null
            ? Failure("report.source.financial-profile-unavailable",
                ContractErrorCategory.DependencyFailure,
                "The financial profile source is unavailable.", correlationId, true)
            : new(error with { CorrelationId = correlationId });

    private static ReportSourceException Failure(
        string code,
        ContractErrorCategory category,
        string message,
        string correlationId,
        bool retryable = false) =>
        new(new ContractError(code, category, message, retryable, correlationId));
}
