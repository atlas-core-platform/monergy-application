using System.Security.Cryptography;
using System.Text;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.Evidence.Application;
using Monergy.Services.Evidence.Infrastructure;

namespace Monergy.Services.Evidence;

public sealed record LocalEvidenceStageRequest(
    ContractRequest<CreateDocumentVersion> Request,
    string ContentBase64);

public sealed record LocalEvidenceStageResult(
    ContractResult<DocumentVersionCreatedResult> Registration,
    string ContentReference,
    string ContentSha256,
    long ContentLength);

public static class EvidenceLocalSetupEndpoints
{
    public static IEndpointRouteBuilder MapD11LocalShutdown(this IEndpointRouteBuilder endpoints,
        IConfiguration configuration)
    {
        var token = PhysicalPersistenceGuard.Require(configuration, "Monergy:D11:ControllerToken");
        endpoints.MapPost("/operations/local/control/stop", (HttpRequest request,
            IHostApplicationLifetime lifetime) =>
        {
            if (!LocalControlToken.IsValid(request.Headers["X-Monergy-Local-Control"].ToString(), token))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            lifetime.StopApplication();
            return Results.Accepted();
        });
        return endpoints;
    }

    public static IEndpointRouteBuilder MapEvidenceLocalSetup(this IEndpointRouteBuilder endpoints,
        IConfiguration configuration)
    {
        PhysicalPersistenceGuard.EnsureAllowed(configuration);
        var expectedToken = PhysicalPersistenceGuard.Require(configuration, "Monergy:D11:LocalSetupToken");
        endpoints.MapPost("/operations/local/evidence/stage", async (
            HttpRequest httpRequest,
            LocalEvidenceStageRequest input,
            S3EvidenceContentStore contentStore,
            EvidenceApplication application,
            CancellationToken cancellationToken) =>
        {
            if (!ValidToken(httpRequest.Headers["X-Monergy-Local-Setup"].ToString(), expectedToken))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            byte[] content;
            try
            {
                content = Convert.FromBase64String(input.ContentBase64);
            }
            catch (FormatException)
            {
                return Results.BadRequest(new { code = "evidence.local.content-invalid" });
            }
            if (content.Length == 0 || content.Length > 1_048_576)
                return Results.BadRequest(new { code = "evidence.local.content-size-invalid" });

            await using var stream = new MemoryStream(content, writable: false);
            var staged = await contentStore.StageAsync(stream, input.Request.Payload.DeclaredContentType,
                cancellationToken).ConfigureAwait(false);
            var payload = input.Request.Payload with
            {
                ContentReference = staged.Descriptor.Reference,
                ContentSha256 = staged.Descriptor.Sha256,
            };
            var request = input.Request with { Payload = payload };
            var registration = await application.CreateDocumentVersionAsync(request, cancellationToken)
                .ConfigureAwait(false);
            return registration.Outcome == ContractOutcome.Success
                ? Results.Ok(new LocalEvidenceStageResult(registration, staged.Descriptor.Reference,
                    staged.Descriptor.Sha256, staged.Descriptor.ContentLength))
                : Results.Json(new LocalEvidenceStageResult(registration, staged.Descriptor.Reference,
                    staged.Descriptor.Sha256, staged.Descriptor.ContentLength), statusCode: 400);
        });
        return endpoints;
    }

    private static bool ValidToken(string actual, string expected)
    {
        var actualBytes = Encoding.UTF8.GetBytes(actual);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        return actualBytes.Length == expectedBytes.Length &&
            CryptographicOperations.FixedTimeEquals(actualBytes, expectedBytes);
    }
}
