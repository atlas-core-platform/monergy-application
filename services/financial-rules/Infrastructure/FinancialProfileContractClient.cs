using System.Net.Http.Json;
using System.Text.Json;
using Monergy.Contracts;
using Monergy.Services.FinancialRules.Application;

namespace Monergy.Services.FinancialRules.Infrastructure;

public sealed class FinancialProfileContractClient(HttpClient client) : IFinancialInputReader
{
    public Task<ContractResult<AuthoritativeFinancialFact>> GetFactAsync(ContractRequest<GetFinancialFact> request, CancellationToken cancellationToken) =>
        SendAsync<GetFinancialFact, AuthoritativeFinancialFact>("contracts/cid-032/v1", request, cancellationToken);

    public Task<ContractResult<FinancialProvenance>> GetProvenanceAsync(ContractRequest<GetFinancialProvenance> request, CancellationToken cancellationToken) =>
        SendAsync<GetFinancialProvenance, FinancialProvenance>("contracts/cid-033/v1", request, cancellationToken);

    private async Task<ContractResult<TResult>> SendAsync<TPayload, TResult>(string path, ContractRequest<TPayload> request, CancellationToken cancellationToken)
    {
        if (client.BaseAddress is null)
        {
            return ContractResult<TResult>.Failed(request, "profile.connection.unconfigured", ContractErrorCategory.DependencyFailure,
                "Authoritative Financial Profile transport is not configured.", true);
        }

        try
        {
            using var response = await client.PostAsJsonAsync(path, request, ContractJson.Options, cancellationToken);
            var result = await response.Content.ReadFromJsonAsync<ContractResult<TResult>>(ContractJson.Options, cancellationToken);
            if (result is null || result.ContractName != request.ContractName || result.ContractVersion != request.ContractVersion ||
                result.RequestId != request.RequestId || result.CorrelationId != request.CorrelationId ||
                (!response.IsSuccessStatusCode && result.Outcome == ContractOutcome.Success))
            {
                throw new HttpRequestException("The authority returned an invalid contract response.");
            }

            return result;
        }
        catch (JsonException exception)
        {
            throw new HttpRequestException("The authority returned malformed contract data.", exception);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HttpRequestException("The authority timed out.", exception);
        }
    }
}
