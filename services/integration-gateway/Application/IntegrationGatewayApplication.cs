using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Monergy.Contracts;
using Monergy.Services.IntegrationGateway.Domain;

namespace Monergy.Services.IntegrationGateway.Application;

public sealed class IntegrationGatewayApplication(
    IConnectorRegistry connectors,
    IGatewayAuthorizationPolicy authorizationPolicy,
    IConsentDecisionPort consentDecisions,
    IGatewayOperationRepository repository,
    IGatewayEventSink eventSink,
    IConnectorRuntimeState runtimeState,
    IGatewayTelemetry telemetry,
    GatewayExecutionOptions options,
    TimeProvider clock)
{
    public async Task<ContractResult<ProviderOperationResult>> ExecuteAsync(
        ContractRequest<ExecuteProviderRequest> request,
        CancellationToken cancellationToken = default)
    {
        if (request.Security is null)
        {
            return ContractResult<ProviderOperationResult>.Rejected(request, "security.authentication.required",
                ContractErrorCategory.AuthenticationRequired, "Trusted actor and workload context are required.");
        }
        var contractError = ContractGuard.Validate(request, IntegrationGatewayContractNames.ExecuteProviderRequest);
        if (contractError is not null) { return Rejected(request, contractError); }

        var validationError = Validate(request);
        if (validationError is not null) { return Rejected(request, validationError); }

        var accessError = await EvaluateAccessAsync(request.Security, request.Payload.CustomerId,
            request.ContractName, request.CorrelationId, cancellationToken).ConfigureAwait(false);
        if (accessError is not null)
        {
            telemetry.Record(new("integration.access", request.Payload.ConnectorId, request.Payload.Operation,
                accessError.Category.ToString(), 0, request.CorrelationId, null));
            return Rejected(request, accessError);
        }

        var connector = connectors.Find(request.Payload.ConnectorId);
        if (connector is null)
        {
            return ContractResult<ProviderOperationResult>.Rejected(request, "integration.connector.unknown",
                ContractErrorCategory.UnsupportedOperation, "The requested connector is not registered.");
        }

        if (!connector.SupportedOperations.Contains(request.Payload.Operation))
        {
            return ContractResult<ProviderOperationResult>.Rejected(request, "integration.operation.unsupported",
                ContractErrorCategory.UnsupportedOperation, "The connector does not support the requested operation.");
        }

        var identity = new GatewayRequestIdentity(request.ContractName, request.ContractVersion,
            request.Payload.CustomerId, request.IdempotencyKey!);
        var fingerprint = Fingerprint(request.Payload);
        try
        {
            var result = await repository.ExecuteAsync(identity, fingerprint,
                () => ExecuteNewAsync(request, connector), cancellationToken).ConfigureAwait(false);
            telemetry.Record(new("integration.idempotency", connector.ConnectorId, request.Payload.Operation,
                result.Disposition.ToString(), result.Execution.Result.AttemptCount, request.CorrelationId,
                result.Execution.Result.ProviderOperationId));
            return ContractResult<ProviderOperationResult>.Succeeded(request, result.Execution.Result);
        }
        catch (GatewayException exception)
        {
            return ContractResult<ProviderOperationResult>.Rejected(request, exception.Code, exception.Category,
                exception.Message, exception.Retryable);
        }
    }

    public async Task<ContractResult<ProviderOperationResult>> GetStatusAsync(
        ContractRequest<GetProviderOperationStatus> request,
        CancellationToken cancellationToken = default)
    {
        if (request.Security is null)
        {
            return ContractResult<ProviderOperationResult>.Rejected(request, "security.authentication.required",
                ContractErrorCategory.AuthenticationRequired, "Trusted actor and workload context are required.");
        }
        var contractError = ContractGuard.Validate(request, IntegrationGatewayContractNames.GetProviderOperationStatus);
        if (contractError is not null) { return Rejected(request, contractError); }
        if (string.IsNullOrWhiteSpace(request.Payload.CustomerId) ||
            string.IsNullOrWhiteSpace(request.Payload.ProviderOperationId) ||
            request.Security.Access.CustomerId != request.Payload.CustomerId)
        {
            return ContractResult<ProviderOperationResult>.Rejected(request, "integration.status.invalid",
                ContractErrorCategory.ValidationError, "Customer and operation identifiers must match current access context.");
        }

        var accessError = await EvaluateAccessAsync(request.Security, request.Payload.CustomerId,
            request.ContractName, request.CorrelationId, cancellationToken).ConfigureAwait(false);
        if (accessError is not null)
        {
            telemetry.Record(new("integration.access", string.Empty, request.ContractName,
                accessError.Category.ToString(), 0, request.CorrelationId, request.Payload.ProviderOperationId));
            return Rejected(request, accessError);
        }

        var result = repository.Find(request.Payload.ProviderOperationId, request.Payload.CustomerId);
        return result is null
            ? ContractResult<ProviderOperationResult>.Rejected(request, "integration.operation.not-found",
                ContractErrorCategory.NotFound, "The operation was not found in the current customer boundary.")
            : ContractResult<ProviderOperationResult>.Succeeded(request, result.Result);
    }

    public ImmutableArray<ConnectorHealth> GetConnectorHealth() =>
        connectors.All().Select(item => runtimeState.GetHealth(item.ConnectorId)).ToImmutableArray();

    private async Task<GatewayExecution> ExecuteNewAsync(
        ContractRequest<ExecuteProviderRequest> request,
        IProviderConnector connector)
    {
        var operationId = StableId("provider-operation", request.ContractName, request.ContractVersion,
            request.Payload.CustomerId, request.IdempotencyKey!);

        for (var attempt = 1; attempt <= options.MaximumAttempts; attempt++)
        {
            // Consent and authorization are deliberately re-evaluated on every attempt.
            var currentAccess = await EvaluateAccessAsync(request.Security, request.Payload.CustomerId,
                request.ContractName, request.CorrelationId, CancellationToken.None).ConfigureAwait(false);
            if (currentAccess is not null)
            {
                telemetry.Record(new("integration.access", connector.ConnectorId, request.Payload.Operation,
                    currentAccess.Category.ToString(), attempt, request.CorrelationId, operationId));
                return await CompleteFailureAsync(request, operationId, connector.ConnectorId, attempt,
                    ProviderOperationState.Failed, currentAccess).ConfigureAwait(false);
            }

            if (!runtimeState.CanExecute(connector.ConnectorId))
            {
                telemetry.Record(new("integration.connector.attempt", connector.ConnectorId, request.Payload.Operation,
                    ConnectorAttemptOutcome.CircuitRejected.ToString(), attempt, request.CorrelationId, operationId));
                var circuitError = new ContractError("integration.circuit.open", ContractErrorCategory.TemporarilyUnavailable,
                    "The connector circuit is open.", true, request.CorrelationId);
                return await CompleteFailureAsync(request, operationId, connector.ConnectorId, attempt,
                    ProviderOperationState.Failed, circuitError).ConfigureAwait(false);
            }

            ConnectorAttemptResult attemptResult;
            try
            {
                attemptResult = await connector.ExecuteAsync(new(operationId, request.Payload.CustomerId,
                    request.Payload.Operation, request.Payload.Fields, request.CorrelationId, attempt),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                attemptResult = ConnectorAttemptResult.Failure(ConnectorAttemptOutcome.UnknownOutcome,
                    "integration.connector.unknown-outcome", true);
            }

            runtimeState.Record(connector.ConnectorId, attemptResult, clock.GetUtcNow());
            telemetry.Record(new("integration.connector.attempt", connector.ConnectorId, request.Payload.Operation,
                attemptResult.Outcome.ToString(), attempt, request.CorrelationId, operationId));

            if (attemptResult.Outcome == ConnectorAttemptOutcome.Succeeded)
            {
                var result = new ProviderOperationResult(operationId, request.Payload.CustomerId, connector.ConnectorId,
                    request.Payload.Operation, ProviderOperationState.Succeeded, attempt,
                    attemptResult.CanonicalResultReferenceId, null, clock.GetUtcNow());
                var message = new DomainEvent<ProviderResultReceivedPayload>("CID-017",
                    StableId("provider-result-event", operationId), IntegrationGatewayContractNames.ProviderResultReceived,
                    ContractGuard.CurrentVersion, clock.GetUtcNow(), request.CorrelationId, request.CausationId,
                    "Integration Gateway Service", "ProviderOperation", operationId,
                    new(operationId, request.Payload.CustomerId, connector.ConnectorId, request.Payload.Operation,
                        attemptResult.CanonicalResultReferenceId!, attempt));
                await eventSink.PublishAsync(message, CancellationToken.None).ConfigureAwait(false);
                return new(result);
            }

            if (attemptResult.Retryable && attempt < options.MaximumAttempts &&
                attemptResult.Outcome is ConnectorAttemptOutcome.TransientFailure or ConnectorAttemptOutcome.TemporarilyUnavailable)
            {
                continue;
            }

            var state = attemptResult.Outcome switch
            {
                ConnectorAttemptOutcome.RateLimited => ProviderOperationState.RateLimited,
                ConnectorAttemptOutcome.UnknownOutcome => ProviderOperationState.UnknownOutcome,
                _ => ProviderOperationState.Failed,
            };
            return await CompleteFailureAsync(request, operationId, connector.ConnectorId, attempt, state,
                ToContractError(attemptResult, request.CorrelationId)).ConfigureAwait(false);
        }

        throw new InvalidOperationException("The bounded retry loop did not produce an operation result.");
    }

    private async Task<GatewayExecution> CompleteFailureAsync(
        ContractRequest<ExecuteProviderRequest> request,
        string operationId,
        string connectorId,
        int attempt,
        ProviderOperationState state,
        ContractError failure)
    {
        var result = new ProviderOperationResult(operationId, request.Payload.CustomerId, connectorId,
            request.Payload.Operation, state, attempt, null, failure, clock.GetUtcNow());
        var message = new DomainEvent<ProviderOperationFailedPayload>("CID-018",
            StableId("provider-failure-event", operationId), IntegrationGatewayContractNames.ProviderOperationFailed,
            ContractGuard.CurrentVersion, clock.GetUtcNow(), request.CorrelationId, request.CausationId,
            "Integration Gateway Service", "ProviderOperation", operationId,
            new(operationId, request.Payload.CustomerId, connectorId, request.Payload.Operation, failure.Code,
                failure.Category, failure.Retryable, state, attempt));
        await eventSink.PublishAsync(message, CancellationToken.None).ConfigureAwait(false);
        return new(result);
    }

    private static ContractError? Validate(ContractRequest<ExecuteProviderRequest> request)
    {
        if (string.IsNullOrWhiteSpace(request.Payload.CustomerId) ||
            request.Payload.CustomerId != request.Security.Access.CustomerId ||
            string.IsNullOrWhiteSpace(request.Payload.ConnectorId) ||
            string.IsNullOrWhiteSpace(request.Payload.Operation))
        {
            return new("integration.request.invalid", ContractErrorCategory.ValidationError,
                "Customer, connector and operation must be present and customer-scoped.", false, request.CorrelationId);
        }

        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            return new("integration.idempotency.required", ContractErrorCategory.PreconditionFailed,
                "An idempotency key is required.", false, request.CorrelationId);
        }

        if (request.Payload.Fields.IsDefault || request.Payload.Fields.Any(field =>
                string.IsNullOrWhiteSpace(field.Name) || field.Value is null) ||
            request.Payload.Fields.Select(field => field.Name).Distinct(StringComparer.Ordinal).Count() != request.Payload.Fields.Length)
        {
            return new("integration.fields.invalid", ContractErrorCategory.ValidationError,
                "Canonical request fields must have unique non-empty names and non-null values.", false, request.CorrelationId);
        }

        return null;
    }

    private static ContractError ToContractError(ConnectorAttemptResult result, string correlationId)
    {
        var category = result.Outcome switch
        {
            ConnectorAttemptOutcome.RateLimited => ContractErrorCategory.RateLimited,
            ConnectorAttemptOutcome.TemporarilyUnavailable or ConnectorAttemptOutcome.UnknownOutcome =>
                ContractErrorCategory.TemporarilyUnavailable,
            ConnectorAttemptOutcome.MalformedProviderResponse => ContractErrorCategory.ProcessingFailed,
            _ => ContractErrorCategory.DependencyFailure,
        };
        return new(result.FailureCode ?? "integration.connector.failed", category,
            "The connector operation did not produce a canonical validated result.", result.Retryable, correlationId);
    }

    private async Task<ContractError?> EvaluateAccessAsync(
        TrustedSecurityContext context,
        string customerId,
        string operation,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var authorization = await authorizationPolicy.AuthorizeAsync(context, customerId, operation,
            correlationId, cancellationToken).ConfigureAwait(false);
        return authorization ?? await consentDecisions.EvaluateAsync(context, customerId, operation,
            correlationId, cancellationToken).ConfigureAwait(false);
    }

    private static ContractResult<ProviderOperationResult> Rejected<TPayload>(ContractRequest<TPayload> request, ContractError error) =>
        new(request.ContractName, request.ContractVersion, request.RequestId, request.CorrelationId,
            ContractOutcome.Rejected, default, error);

    private static string Fingerprint(ExecuteProviderRequest payload)
    {
        var fields = string.Join("\n", payload.Fields.OrderBy(item => item.Name, StringComparer.Ordinal)
            .Select(item => $"{item.Name.Length}:{item.Name}{item.Value.Length}:{item.Value}"));
        return StableId("request", payload.CustomerId, payload.ConnectorId, payload.Operation, fields);
    }

    private static string StableId(string prefix, params string[] values)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\u001f", values)));
        return $"{prefix}-{Convert.ToHexString(bytes).ToLowerInvariant()[..24]}";
    }
}
