using System.Collections.Immutable;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.Reporting.Application;
using Monergy.Services.Reporting.Domain;

namespace Monergy.Services.Reporting.Infrastructure;

public sealed class ServiceContractReportSourceReader : IReportSourceReader
{
    public const string FinancialProfileClient = "d11-financial-profile";
    public const string FinancialRulesClient = "d11-financial-rules";
    public const string EvidenceClient = "d11-evidence";

    private readonly IHttpClientFactory clients;
    private readonly string scenarioStatePath;
    private readonly string workloadIdentityId;

    public ServiceContractReportSourceReader(IHttpClientFactory clients, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(configuration);
        PhysicalPersistenceGuard.EnsureAllowed(configuration);
        if (!string.Equals(configuration["Monergy:D11:Profile"], "persisted-reporting", StringComparison.Ordinal))
            throw new InvalidOperationException("The service-contract report reader is restricted to the persisted-reporting profile.");
        this.clients = clients;
        scenarioStatePath = PhysicalPersistenceGuard.Require(configuration, "Monergy:D11:ScenarioStatePath");
        workloadIdentityId = configuration["Monergy:D11:ReportingWorkloadIdentityId"] ?? "d11-reporting-workload";
    }

    public async Task<ReportSourceSnapshot?> ReadAsync(
        ReportSourceReadContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        var scenario = await ReadScenarioAsync(context.CustomerId, cancellationToken).ConfigureAwait(false);
        if (scenario is null) return null;
        ValidateScenario(scenario, context.CustomerId, context.CorrelationId);

        var security = context.Security with
        {
            Workload = new WorkloadContext("reporting", workloadIdentityId),
            Access = context.Security.Access with
            {
                AuthorizationContextId = $"d11-reporting-{context.CustomerId}-authorization",
            },
        };
        var profile = await SendAsync<GetFinancialProfile, FinancialProfileDetails>(FinancialProfileClient,
            "contracts/cid-030/v1", Request(context, security, Vs02ContractNames.GetFinancialProfile,
                new GetFinancialProfile(scenario.FinancialProfileId, context.CustomerId)), cancellationToken)
            .ConfigureAwait(false);
        EnsureCustomer(profile.CustomerId, context.CustomerId, "report.source.profile-boundary-invalid", context.CorrelationId);
        EnsureIdentity(profile.FinancialProfileId, scenario.FinancialProfileId,
            "report.source.profile-identity-invalid", "Financial Profile returned a different profile.", context.CorrelationId);

        var facts = new List<(AuthoritativeFinancialFact Fact, FinancialProvenance Provenance, EvidenceReference SourceReference)>();
        foreach (var factId in scenario.FinancialFactIds)
        {
            var fact = await SendAsync<GetFinancialFact, AuthoritativeFinancialFact>(FinancialProfileClient,
                "contracts/cid-032/v1", Request(context, security, Vs02ContractNames.GetFinancialFact,
                    new GetFinancialFact(factId, context.CustomerId)), cancellationToken).ConfigureAwait(false);
            EnsureCustomer(fact.CustomerId, context.CustomerId, "report.source.fact-boundary-invalid", context.CorrelationId);
            EnsureIdentity(fact.FinancialFactId, factId, "report.source.fact-identity-invalid",
                "Financial Profile returned a different requested fact.", context.CorrelationId);
            EnsureIdentity(fact.FinancialProfileId, scenario.FinancialProfileId,
                "report.source.fact-profile-invalid", "Financial fact belongs to a different profile.", context.CorrelationId);
            if (profile.Facts is null || profile.Facts.Any(value => value is null))
                throw Invalid("report.source.profile-inconsistent", "Financial Profile returned malformed fact state.", context.CorrelationId);
            var profileFacts = profile.Facts.Where(value => value.FinancialFactId == fact.FinancialFactId).Take(2).ToArray();
            if (profileFacts.Length != 1 || profileFacts[0] != fact)
                throw Invalid("report.source.profile-inconsistent", "Financial Profile returned inconsistent fact state.", context.CorrelationId);

            var provenance = await SendAsync<GetFinancialProvenance, FinancialProvenance>(FinancialProfileClient,
                "contracts/cid-033/v1", Request(context, security, Vs02ContractNames.GetFinancialProvenance,
                    new GetFinancialProvenance(fact.FinancialProvenanceId, context.CustomerId)), cancellationToken)
                .ConfigureAwait(false);
            EnsureCustomer(provenance.CustomerId, context.CustomerId, "report.source.provenance-boundary-invalid", context.CorrelationId);
            EnsureIdentity(provenance.FinancialProvenanceId, fact.FinancialProvenanceId,
                "report.source.provenance-identity-invalid", "Financial Profile returned a different requested provenance record.",
                context.CorrelationId);
            if (provenance.FinancialFactId != fact.FinancialFactId || string.IsNullOrWhiteSpace(provenance.SourceFactId))
                throw Invalid("report.source.provenance-inconsistent", "Financial provenance does not identify its authoritative fact.", context.CorrelationId);

            var sourceReference = await SendAsync<GetEvidenceReference, EvidenceReference>(EvidenceClient,
                "contracts/cid-022/v1", Request(context, security, Vs02ContractNames.GetEvidenceReference,
                    new GetEvidenceReference(provenance.DocumentVersionId, context.CustomerId)), cancellationToken)
                .ConfigureAwait(false);
            EnsureCustomer(sourceReference.CustomerId, context.CustomerId, "report.source.evidence-boundary-invalid", context.CorrelationId);
            if (sourceReference.EvidenceId != provenance.EvidenceId ||
                sourceReference.DocumentVersionId != provenance.DocumentVersionId ||
                sourceReference.DocumentId != scenario.DocumentId ||
                sourceReference.DocumentVersionId != scenario.DocumentVersionId ||
                sourceReference.EvidenceId != scenario.EvidenceId ||
                string.IsNullOrWhiteSpace(sourceReference.ContentReference) ||
                string.IsNullOrWhiteSpace(sourceReference.ContentSha256) ||
                string.IsNullOrWhiteSpace(sourceReference.ContentType))
                throw Invalid("report.source.evidence-inconsistent", "Evidence does not match financial provenance.", context.CorrelationId);
            facts.Add((fact, provenance, sourceReference));
        }

        var metadata = await SendAsync<GetEvidenceMetadata, EvidenceMetadata>(EvidenceClient,
            "contracts/cid-021/v1", Request(context, security, Vs02ContractNames.GetEvidenceMetadata,
                new GetEvidenceMetadata(scenario.DocumentId, context.CustomerId)), cancellationToken)
            .ConfigureAwait(false);
        EnsureCustomer(metadata.CustomerId, context.CustomerId, "report.source.metadata-boundary-invalid", context.CorrelationId);
        EnsureIdentity(metadata.DocumentId, scenario.DocumentId, "report.source.metadata-identity-invalid",
            "Evidence returned metadata for a different document.", context.CorrelationId);
        if (metadata.Versions is null || metadata.Versions.Any(version => version is null))
            throw Invalid("report.source.evidence-version-inconsistent", "Evidence metadata contains malformed version state.", context.CorrelationId);
        var selectedVersions = metadata.Versions.Where(version =>
            version.DocumentVersionId == scenario.DocumentVersionId && version.EvidenceId == scenario.EvidenceId).Take(2).ToArray();
        if (selectedVersions.Length != 1)
            throw Invalid("report.source.evidence-version-inconsistent", "Evidence metadata does not identify exactly one selected persisted version.", context.CorrelationId);
        var selectedVersion = selectedVersions[0];
        if (facts.Any(value =>
                value.SourceReference.DocumentId != metadata.DocumentId ||
                value.SourceReference.DocumentVersionId != selectedVersion.DocumentVersionId ||
                value.SourceReference.EvidenceId != selectedVersion.EvidenceId ||
                !string.Equals(value.SourceReference.ContentSha256, selectedVersion.ContentSha256,
                    StringComparison.OrdinalIgnoreCase) ||
                value.SourceReference.ContentType != selectedVersion.ContentType))
            throw Invalid("report.source.evidence-version-inconsistent", "Evidence metadata does not contain the selected persisted version.", context.CorrelationId);

        var calculation = await SendAsync<GetCalculationResult, CalculationResult>(FinancialRulesClient,
            "contracts/cid-038/v1", Request(context, security, FinancialRulesContractNames.GetCalculationResult,
                new GetCalculationResult(context.CustomerId, scenario.CalculationResultId)), cancellationToken)
            .ConfigureAwait(false);
        EnsureCustomer(calculation.CustomerId, context.CustomerId, "report.source.calculation-boundary-invalid", context.CorrelationId);
        EnsureIdentity(calculation.CalculationResultId, scenario.CalculationResultId,
            "report.source.calculation-identity-invalid", "Financial Rules returned a different requested result.",
            context.CorrelationId);
        var explanation = await SendAsync<ExplainCalculation, CalculationExplanation>(FinancialRulesClient,
            "contracts/cid-039/v1", Request(context, security, FinancialRulesContractNames.ExplainCalculation,
                new ExplainCalculation(context.CustomerId, scenario.CalculationResultId)), cancellationToken)
            .ConfigureAwait(false);
        if (explanation.Result is null)
            throw Invalid("report.source.response-malformed", "Calculation explanation did not contain a result.", context.CorrelationId);
        EnsureCustomer(explanation.Result.CustomerId, context.CustomerId,
            "report.source.explanation-boundary-invalid", context.CorrelationId);
        if (explanation.Result.CalculationResultId != scenario.CalculationResultId ||
            !CalculationResultsMatch(calculation, explanation.Result))
            throw Invalid("report.source.calculation-inconsistent", "Calculation result and explanation are inconsistent.", context.CorrelationId);

        if (calculation.Revision <= 0 || string.IsNullOrWhiteSpace(calculation.CalculationLineageId) ||
            calculation.RuleId != "engineering.sum" || calculation.RuleVersion != ContractGuard.CurrentVersion ||
            calculation.Inputs.IsDefaultOrEmpty ||
            calculation.Inputs.Any(input => string.IsNullOrWhiteSpace(input.Role) ||
                string.IsNullOrWhiteSpace(input.FinancialFactId)) ||
            calculation.Inputs.Select(input => input.Role).Distinct(StringComparer.Ordinal).Count() != calculation.Inputs.Length ||
            calculation.Inputs.Select(input => input.FinancialFactId).Distinct(StringComparer.Ordinal).Count() != calculation.Inputs.Length ||
            !calculation.Inputs.Select(input => input.FinancialFactId).OrderBy(value => value, StringComparer.Ordinal)
                .SequenceEqual(scenario.FinancialFactIds.OrderBy(value => value, StringComparer.Ordinal), StringComparer.Ordinal))
            throw Invalid("report.source.calculation-lineage-invalid",
                "Calculation lineage does not identify the selected engineering fixture source set.", context.CorrelationId);

        foreach (var input in calculation.Inputs)
        {
            var source = facts.SingleOrDefault(value => value.Fact.FinancialFactId == input.FinancialFactId);
            if (source.Fact is null || source.Fact.Revision != input.FactRevision ||
                source.Fact.Value != input.Value || source.Fact.Currency != input.Unit ||
                source.Provenance != input.Provenance)
                throw Invalid("report.source.lineage-inconsistent", "Calculation lineage does not match persisted source revisions.", context.CorrelationId);
        }

        var items = facts.Select(value => new ReportLineItem(value.Fact.Label, value.Fact.Value,
                value.Fact.Currency, new ReportSourceReference("FinancialFact", value.Fact.FinancialFactId,
                    ReportingAuthority.FinancialProfileService, value.SourceReference.EvidenceId,
                    value.Provenance.FinancialProvenanceId, null)))
            .Append(new ReportLineItem($"Engineering fixture {calculation.RuleId}", calculation.Value,
                calculation.Unit, new ReportSourceReference("FinancialCalculation",
                    calculation.CalculationResultId, ReportingAuthority.FinancialRulesService, null,
                    calculation.Inputs.FirstOrDefault()?.Provenance.FinancialProvenanceId,
                    calculation.CalculationLineageId)))
            .OrderBy(value => value.Label, StringComparer.Ordinal)
            .ToImmutableArray();
        var asOf = new[] { profile.UpdatedAt, calculation.ExecutedAt, metadata.Versions.Max(value => value.ReceivedAt) }.Max();
        return new ReportSourceSnapshot(context.CustomerId, asOf, items, null);
    }

    private async Task<LocalReportingScenario?> ReadScenarioAsync(string customerId, CancellationToken cancellationToken)
    {
        if (!File.Exists(scenarioStatePath)) return null;
        await using var stream = File.OpenRead(scenarioStatePath);
        LocalReportingScenarioState state;
        try
        {
            state = await JsonSerializer.DeserializeAsync<LocalReportingScenarioState>(stream,
                ContractJson.Options, cancellationToken).ConfigureAwait(false)
                ?? throw Invalid("report.source.scenario-invalid", "The D11 scenario state is empty.", string.Empty);
        }
        catch (JsonException)
        {
            throw new ReportSourceException(new("report.source.scenario-invalid",
                ContractErrorCategory.DependencyFailure, "The D11 scenario state is malformed.", false, string.Empty));
        }
        if (state.Profile != "persisted-reporting" || state.Customers is null ||
            state.Customers.Any(value => value is null) ||
            state.Customers.GroupBy(value => value!.CustomerId, StringComparer.Ordinal).Any(group => group.Count() != 1))
            throw Invalid("report.source.scenario-invalid", "The D11 scenario state is structurally invalid.", string.Empty);
        return state.Customers.SingleOrDefault(value => value!.CustomerId == customerId);
    }

    private async Task<TResult> SendAsync<TPayload, TResult>(string clientName, string path,
        ContractRequest<TPayload> request, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await clients.CreateClient(clientName)
                .PostAsJsonAsync(path, request, ContractJson.Options, cancellationToken).ConfigureAwait(false);
            var result = await response.Content.ReadFromJsonAsync<ContractResult<TResult>>(
                ContractJson.Options, cancellationToken).ConfigureAwait(false);
            if (result is null || result.ContractName != request.ContractName ||
                result.ContractVersion != request.ContractVersion || result.RequestId != request.RequestId ||
                result.CorrelationId != request.CorrelationId ||
                (!response.IsSuccessStatusCode && result.Outcome == ContractOutcome.Success))
                throw new HttpRequestException("An authoritative source returned an invalid contract response.");
            if (result.Outcome != ContractOutcome.Success || result.Data is null)
                throw new ReportSourceException(result.Error ?? new ContractError("report.source.rejected",
                    ContractErrorCategory.DependencyFailure, "An authoritative report source rejected the read.",
                    true, request.CorrelationId));
            return result.Data;
        }
        catch (JsonException exception)
        {
            throw new ReportSourceException(new("report.source.response-malformed",
                ContractErrorCategory.DependencyFailure,
                $"An authoritative source returned malformed contract data: {exception.GetType().Name}.",
                false, request.CorrelationId));
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HttpRequestException("An authoritative source timed out.", exception);
        }
    }

    private static ContractRequest<TPayload> Request<TPayload>(ReportSourceReadContext source,
        TrustedSecurityContext security, string contractName, TPayload payload)
    {
        var suffix = contractName.ToLowerInvariant();
        return new(contractName, ContractGuard.CurrentVersion, $"{source.RequestId}-{suffix}",
            source.CorrelationId, source.RequestId, security, null, payload);
    }

    private static void EnsureCustomer(string actual, string expected, string code, string correlationId)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw Invalid(code, "An authoritative source crossed the current customer boundary.", correlationId);
    }

    private static void EnsureIdentity(string actual, string expected, string code, string message,
        string correlationId)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw Invalid(code, message, correlationId);
    }

    private static void ValidateScenario(LocalReportingScenario scenario, string customerId, string correlationId)
    {
        if (scenario.CustomerId != customerId || string.IsNullOrWhiteSpace(scenario.FinancialProfileId) ||
            string.IsNullOrWhiteSpace(scenario.CalculationResultId) || string.IsNullOrWhiteSpace(scenario.DocumentId) ||
            string.IsNullOrWhiteSpace(scenario.DocumentVersionId) || string.IsNullOrWhiteSpace(scenario.EvidenceId) ||
            scenario.FinancialFactIds is null || scenario.FinancialFactIds.Count == 0 ||
            scenario.FinancialFactIds.Any(string.IsNullOrWhiteSpace) ||
            scenario.FinancialFactIds.Distinct(StringComparer.Ordinal).Count() != scenario.FinancialFactIds.Count)
            throw Invalid("report.source.scenario-invalid", "The selected D11 scenario is structurally invalid.", correlationId);
    }

    private static ReportSourceException Invalid(string code, string message, string correlationId) =>
        new(new ContractError(code, ContractErrorCategory.DependencyFailure, message, false, correlationId));

    private static bool CalculationResultsMatch(CalculationResult expected, CalculationResult actual)
    {
        if (expected.CalculationResultId != actual.CalculationResultId || expected.Revision != actual.Revision ||
            expected.CalculationLineageId != actual.CalculationLineageId || expected.CustomerId != actual.CustomerId ||
            expected.RuleId != actual.RuleId || expected.RuleVersion != actual.RuleVersion ||
            expected.ImplementationIdentity != actual.ImplementationIdentity ||
            expected.DefinitionHash != actual.DefinitionHash || expected.NumericSemantics != actual.NumericSemantics ||
            expected.Value != actual.Value || expected.Unit != actual.Unit || expected.Operation != actual.Operation ||
            expected.ExecutedAt != actual.ExecutedAt || expected.ActorId != actual.ActorId ||
            expected.WorkloadIdentityId != actual.WorkloadIdentityId || expected.Purpose != actual.Purpose ||
            expected.CorrelationId != actual.CorrelationId || expected.CausationId != actual.CausationId ||
            expected.OutcomeEventId != actual.OutcomeEventId || expected.Inputs.IsDefault != actual.Inputs.IsDefault)
            return false;
        return !expected.Inputs.IsDefault && expected.Inputs.SequenceEqual(actual.Inputs);
    }

    private sealed record LocalReportingScenarioState(
        string Profile,
        IReadOnlyList<LocalReportingScenario?> Customers);

    private sealed record LocalReportingScenario(
        string CustomerId,
        string FinancialProfileId,
        IReadOnlyList<string> FinancialFactIds,
        string CalculationResultId,
        string DocumentId,
        string DocumentVersionId,
        string EvidenceId);
}
