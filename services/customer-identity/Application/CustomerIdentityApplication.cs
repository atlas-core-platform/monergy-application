using System.Security.Cryptography;
using System.Text.Json;
using Monergy.Contracts;

namespace Monergy.Services.CustomerIdentity.Application;

public sealed class CustomerIdentityApplication(
    ICustomerAuthenticationProvider authenticationProvider,
    ICustomerIdentityRepository repository,
    ICustomerIdentityTelemetry telemetry,
    TimeProvider clock,
    TrustedSessionLifecycle trustedSessions)
{
    public Task<ContractResult<CustomerProjection>> GetCustomerAsync(
        ContractRequest<GetCustomer> request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var customerId = request.Payload?.CustomerId ?? string.Empty;
        var validation = ValidateRequest(request, D13ContractNames.GetCustomer);
        if (validation is not null)
        {
            return Task.FromResult(Complete(request, customerId,
                Reject<CustomerProjection, GetCustomer>(request, validation)));
        }

        if (!IsBounded(request.Payload!.CustomerId, 128))
        {
            return Task.FromResult(Complete(request, customerId,
                Reject<CustomerProjection, GetCustomer>(request,
                Error("customer.request.invalid", ContractErrorCategory.ValidationError,
                    "A canonical Customer identifier is required.", request.CorrelationId))));
        }

        if (!MatchesCustomer(request.Security, request.Payload.CustomerId))
        {
            return Task.FromResult(Complete(request, customerId,
                Reject<CustomerProjection, GetCustomer>(request,
                Error("customer.access.denied", ContractErrorCategory.AccessDenied,
                    "The requested Customer is outside the trusted customer context.", request.CorrelationId))));
        }

        var read = repository.GetCustomer(request.Payload.CustomerId);
        var result = read.Failure switch
        {
            RepositoryFailure.None => ContractResult<CustomerProjection>.Succeeded(request, read.Value!),
            RepositoryFailure.NotFound => ContractResult<CustomerProjection>.Rejected(request,
                "customer.not-found", ContractErrorCategory.NotFound,
                "The Customer was not found in the current customer boundary."),
            _ => ContractResult<CustomerProjection>.Failed(request, "customer.repository.unavailable",
                ContractErrorCategory.DependencyFailure, "The Customer repository is unavailable.", true),
        };
        return Task.FromResult(Complete(request, customerId, result));
    }

    public async Task<ContractResult<ActorContext>> GetTrustedActorContextAsync(
        ContractRequest<GetTrustedActorContext> request,
        CancellationToken cancellationToken = default)
    {
        var customerId = request.Payload?.CustomerId ?? string.Empty;
        var validation = ValidateRequest(request, D13ContractNames.GetTrustedActorContext);
        if (validation is not null)
        {
            return Complete(request, customerId,
                Reject<ActorContext, GetTrustedActorContext>(request, validation));
        }

        if (!IsBounded(request.Payload!.CustomerId, 128) ||
            !IsBounded(request.Payload.AuthenticationReference, 256))
        {
            return Complete(request, customerId,
                Reject<ActorContext, GetTrustedActorContext>(request,
                Error("identity.authentication.reference.invalid", ContractErrorCategory.AuthenticationRequired,
                    "A verifiable authentication reference is required.", request.CorrelationId)));
        }

        if (!MatchesCustomer(request.Security, request.Payload.CustomerId))
        {
            return Complete(request, customerId,
                Reject<ActorContext, GetTrustedActorContext>(request,
                Error("identity.customer.mismatch", ContractErrorCategory.AccessDenied,
                    "The trusted customer context does not match the requested Customer.", request.CorrelationId)));
        }

        AuthenticationResolution resolved;
        try
        {
            resolved = await authenticationProvider.ResolveAsync(
                new(request.Payload.AuthenticationReference, request.Payload.CustomerId), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return Complete(request, customerId, ContractResult<ActorContext>.Failed(request,
                "identity.authentication.provider-failed", ContractErrorCategory.DependencyFailure,
                "The authentication decision is temporarily unavailable.", true));
        }

        ContractResult<ActorContext> result;
        if (resolved is null)
        {
            result = ContractResult<ActorContext>.Rejected(request, "identity.authentication.malformed",
                ContractErrorCategory.AuthenticationRequired,
                "The authentication reference could not be verified.");
        }
        else if (resolved.Actor is null || resolved.CustomerId is null || resolved.FailureCategory is not null)
        {
            var category = resolved.FailureCategory ?? ContractErrorCategory.AuthenticationRequired;
            result = category is ContractErrorCategory.AuthenticationRequired or ContractErrorCategory.AccessDenied
                ? ContractResult<ActorContext>.Rejected(request,
                    category == ContractErrorCategory.AccessDenied
                        ? "identity.customer.mismatch"
                        : "identity.authentication.unverifiable",
                    category,
                    "The authentication reference could not be verified.", resolved.Retryable)
                : ContractResult<ActorContext>.Failed(request,
                    "identity.authentication.provider-failed", category,
                    "The authentication decision is temporarily unavailable.", resolved.Retryable);
        }
        else if (!CustomerIdentityBoundaryValidation.IsValidCanonicalResolution(resolved))
        {
            result = ContractResult<ActorContext>.Rejected(request, "identity.authentication.malformed",
                ContractErrorCategory.AuthenticationRequired,
                "The authentication reference could not be verified.");
        }
        else if (!string.Equals(resolved.CustomerId, request.Payload.CustomerId, StringComparison.Ordinal))
        {
            result = ContractResult<ActorContext>.Rejected(request, "identity.customer.mismatch",
                ContractErrorCategory.AccessDenied,
                "The authentication result does not match the trusted customer context.");
        }
        else
        {
            TrustedSessionEvaluation session;
            try
            {
                session = trustedSessions.EstablishOrEvaluate(resolved.CustomerId, resolved.Actor,
                    request.RequestId, request.CorrelationId);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                session = new(TrustedSessionEvaluationStatus.DependencyFailure, null, false);
            }

            result = session.Status switch
            {
                TrustedSessionEvaluationStatus.Current when session.Session is not null =>
                    ContractResult<ActorContext>.Succeeded(request, session.Session.Actor),
                TrustedSessionEvaluationStatus.DependencyFailure =>
                    ContractResult<ActorContext>.Failed(request, "identity.session.unavailable",
                        ContractErrorCategory.DependencyFailure,
                        "The current trusted authentication context is temporarily unavailable.", true),
                _ => ContractResult<ActorContext>.Rejected(request, "identity.session.not-current",
                    ContractErrorCategory.AuthenticationRequired,
                    "A current trusted authentication context is required."),
            };
        }

        return Complete(request, customerId, result);
    }

    public Task<ContractResult<CustomerProjection>> RegisterOrUpdateCustomerAsync(
        ContractRequest<RegisterOrUpdateCustomer> request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var customerId = request.Payload?.CustomerId ?? string.Empty;
        var validation = ValidateRequest(request, D13ContractNames.RegisterOrUpdateCustomer);
        if (validation is not null)
        {
            return Task.FromResult(Complete(request, customerId,
                Reject<CustomerProjection, RegisterOrUpdateCustomer>(request, validation)));
        }

        if (!IsBounded(request.Payload!.CustomerId, 128) ||
            !IsCanonicalText(request.Payload.DisplayName, 200) ||
            request.Payload.ExpectedRevision < 0)
        {
            return Task.FromResult(Complete(request, customerId,
                Reject<CustomerProjection, RegisterOrUpdateCustomer>(request,
                Error("customer.mutation.invalid", ContractErrorCategory.ValidationError,
                    "Canonical Customer fields and a non-negative expected revision are required.", request.CorrelationId))));
        }

        if (!MatchesCustomer(request.Security, request.Payload.CustomerId))
        {
            return Task.FromResult(Complete(request, customerId,
                Reject<CustomerProjection, RegisterOrUpdateCustomer>(request,
                Error("customer.access.denied", ContractErrorCategory.AccessDenied,
                    "The Customer mutation is outside the trusted customer context.", request.CorrelationId))));
        }

        if (!IsBounded(request.IdempotencyKey, 256))
        {
            return Task.FromResult(Complete(request, customerId,
                Reject<CustomerProjection, RegisterOrUpdateCustomer>(request,
                Error("customer.idempotency.required", ContractErrorCategory.PreconditionFailed,
                    "A stable idempotency key is required.", request.CorrelationId))));
        }

        var mutation = repository.RegisterOrUpdateCustomer(
            new(request.ContractName, request.ContractVersion, request.Payload.CustomerId, request.IdempotencyKey!),
            Fingerprint(request.Payload), request.Payload, clock.GetUtcNow());
        var result = mutation.Failure switch
        {
            RepositoryFailure.None => ContractResult<CustomerProjection>.Succeeded(request, mutation.Value!),
            RepositoryFailure.DuplicateRequest => ContractResult<CustomerProjection>.Rejected(request,
                "customer.idempotency.conflict", ContractErrorCategory.DuplicateRequest,
                "The idempotency identity was already used for different semantic input."),
            RepositoryFailure.Conflict => ContractResult<CustomerProjection>.Rejected(request,
                "customer.revision.conflict", ContractErrorCategory.Conflict,
                "The Customer revision changed before this mutation could be applied."),
            _ => ContractResult<CustomerProjection>.Failed(request, "customer.repository.unavailable",
                ContractErrorCategory.DependencyFailure, "The Customer repository is unavailable.", true),
        };
        return Task.FromResult(Complete(request, customerId, result));
    }

    public Task<ContractResult<KycVerification>> RecordKycResultAsync(
        ContractRequest<RecordKycResult> request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var customerId = request.Payload?.CustomerId ?? string.Empty;
        var validation = ValidateRequest(request, D13ContractNames.RecordKycResult);
        if (validation is not null)
        {
            return Task.FromResult(Complete(request, customerId,
                Reject<KycVerification, RecordKycResult>(request, validation)));
        }

        if (!IsBounded(request.Payload!.CustomerId, 128) ||
            !IsBounded(request.Payload.VerificationId, 128) ||
            !IsCanonicalText(request.Payload.StatusCode, 64) ||
            request.Payload.DeterminedAt == default)
        {
            return Task.FromResult(Complete(request, customerId,
                Reject<KycVerification, RecordKycResult>(request,
                Error("kyc.result.invalid", ContractErrorCategory.ValidationError,
                    "Canonical KYC result fields are required.", request.CorrelationId))));
        }

        if (!MatchesCustomer(request.Security, request.Payload.CustomerId))
        {
            return Task.FromResult(Complete(request, customerId,
                Reject<KycVerification, RecordKycResult>(request,
                Error("kyc.access.denied", ContractErrorCategory.AccessDenied,
                    "The KYC result is outside the trusted customer context.", request.CorrelationId))));
        }

        if (!IsBounded(request.IdempotencyKey, 256))
        {
            return Task.FromResult(Complete(request, customerId,
                Reject<KycVerification, RecordKycResult>(request,
                Error("kyc.idempotency.required", ContractErrorCategory.PreconditionFailed,
                    "A stable idempotency key is required.", request.CorrelationId))));
        }

        var mutation = repository.RecordKycResult(
            new(request.ContractName, request.ContractVersion, request.Payload.CustomerId, request.IdempotencyKey!),
            Fingerprint(request.Payload), request.Payload, clock.GetUtcNow());
        var result = mutation.Failure switch
        {
            RepositoryFailure.None => ContractResult<KycVerification>.Succeeded(request, mutation.Value!),
            RepositoryFailure.NotFound => ContractResult<KycVerification>.Rejected(request,
                "kyc.customer.not-found", ContractErrorCategory.NotFound,
                "The Customer was not found in the current customer boundary."),
            RepositoryFailure.DuplicateRequest => ContractResult<KycVerification>.Rejected(request,
                "kyc.idempotency.conflict", ContractErrorCategory.DuplicateRequest,
                "The idempotency identity was already used for different semantic input."),
            _ => ContractResult<KycVerification>.Failed(request, "kyc.repository.unavailable",
                ContractErrorCategory.DependencyFailure, "The KYC repository is unavailable.", true),
        };
        return Task.FromResult(Complete(request, customerId, result));
    }

    private static ContractError? ValidateRequest<TPayload>(ContractRequest<TPayload> request, string expectedName)
    {
        if (request.Security is null)
        {
            return Error("security.authentication.required", ContractErrorCategory.AuthenticationRequired,
                "Trusted actor and workload context are required.", request.CorrelationId);
        }

        var contractError = ContractGuard.Validate(request, expectedName);
        if (contractError is not null)
        {
            return contractError;
        }

        if (!CustomerIdentityBoundaryValidation.IsSafeIdentifier(
                request.RequestId, CustomerIdentityBoundaryValidation.RequestIdMaximumLength) ||
            !CustomerIdentityBoundaryValidation.IsSafeIdentifier(
                request.CorrelationId, CustomerIdentityBoundaryValidation.CorrelationIdMaximumLength) ||
            !CustomerIdentityBoundaryValidation.IsSafeIdentifier(
                request.Security.Access.CustomerId, CustomerIdentityBoundaryValidation.CustomerIdMaximumLength))
        {
            return Error("contract.telemetry-context.invalid", ContractErrorCategory.ValidationError,
                "Safe request, correlation and customer identifiers are required.", request.CorrelationId);
        }

        return request.Payload is null
            ? Error("contract.payload.invalid", ContractErrorCategory.ValidationError,
                "A contract payload is required.", request.CorrelationId)
            : null;
    }

    private static bool MatchesCustomer(TrustedSecurityContext security, string customerId) =>
        string.Equals(security.Access.CustomerId, customerId, StringComparison.Ordinal);

    private static bool IsBounded(string? value, int maximumLength) =>
        CustomerIdentityBoundaryValidation.IsSafeIdentifier(value, maximumLength);

    private static bool IsCanonicalText(string? value, int maximumLength) =>
        IsBounded(value, maximumLength) && value is not null && !value.Any(char.IsControl);

    private static string Fingerprint<TPayload>(TPayload payload) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(payload, ContractJson.Options)))
            .ToLowerInvariant();

    private static ContractError Error(
        string code,
        ContractErrorCategory category,
        string message,
        string correlationId) =>
        new(code, category, message, false, correlationId ?? string.Empty);

    private static ContractResult<TData> Reject<TData, TPayload>(
        ContractRequest<TPayload> request,
        ContractError error) =>
        new(request.ContractName, request.ContractVersion, request.RequestId, request.CorrelationId,
            ContractOutcome.Rejected, default, error);

    private ContractResult<TData> Complete<TPayload, TData>(
        ContractRequest<TPayload> request,
        string customerId,
        ContractResult<TData> result)
    {
        Record(request, customerId, result);
        return result;
    }

    private void Record<TPayload, TData>(
        ContractRequest<TPayload> request,
        string customerId,
        ContractResult<TData> result)
    {
        var signal = new CustomerIdentityTelemetrySignal(request.ContractName, result.Outcome.ToString(),
            customerId, request.RequestId, request.CorrelationId);
        if (!CustomerIdentityBoundaryValidation.IsSafeTelemetrySignal(signal))
        {
            return;
        }

        telemetry.Record(signal);
    }
}
