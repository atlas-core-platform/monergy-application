using System.Collections.Immutable;
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
    private static readonly HashSet<string> SupportedFactTypes = new(StringComparer.Ordinal)
    { "INCOME", "EXPENSE", "LOAN", "ASSET", "INVESTMENT", "INSURANCE", "TAX" };
    private readonly Uri endpoint = TenantAccessIntegration.LoopbackRoot(
        TenantAccessIntegration.Required(configuration, "Monergy:ProductReporting:FinancialProfileUrl"));

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

        if (profileResult.Outcome == ContractOutcome.Rejected &&
            profileResult.Error?.Category == ContractErrorCategory.NotFound)
            return null;
        if (profileResult.Outcome != ContractOutcome.Success || profileResult.Data is null)
            throw OwnerFailure(profileResult.Error, context.CorrelationId);

        var profile = profileResult.Data;
        if (profile.CustomerId != context.CustomerId || profile.FinancialProfileId != profileId ||
            profile.Facts.Any(fact =>
                fact.CustomerId != context.CustomerId ||
                fact.FinancialProfileId != profileId ||
                string.IsNullOrWhiteSpace(fact.FinancialFactId) ||
                string.IsNullOrWhiteSpace(fact.FinancialProvenanceId)) ||
            profile.Facts.Select(fact => fact.FinancialFactId).Distinct(StringComparer.Ordinal).Count() !=
                profile.Facts.Count)
        {
            throw Failure("report.source.profile-boundary-invalid", ContractErrorCategory.AccessDenied,
                "The financial profile response is outside the current customer boundary.",
                context.CorrelationId);
        }

        var supported = profile.Facts
            .Select(fact => fact with { FactType = AssetSourceFactBoundary.ForCustomer(fact.FactType) })
            .Where(fact => SupportedFactTypes.Contains(fact.FactType))
            .OrderBy(fact => fact.FactType, StringComparer.Ordinal)
            .ThenBy(fact => fact.Label, StringComparer.Ordinal)
            .ToArray();
        if (supported.Length == 0) return null;

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

        return new ReportSourceSnapshot(
            context.CustomerId,
            profile.UpdatedAt,
            items.ToImmutable(),
            null);
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
