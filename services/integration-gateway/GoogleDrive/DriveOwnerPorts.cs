using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Monergy.Contracts;
using Monergy.Platform;

namespace Monergy.Services.IntegrationGateway.GoogleDrive;

public sealed record DriveCustomer(CustomerOwnerSession Session, CustomerOwnerWorkspace Workspace);
public sealed record DriveConsentDecision(string ConnectionId, string CustomerId, string Provider, string Purpose,
    string? FolderId, long Version, bool Allowed, DateTimeOffset ExpiresAt);
public interface IDriveOwners
{
    Task<DriveCustomer> AuthorizeAsync(HttpContext http);
    Task<DriveConsentDecision> ConsentAsync(HttpContext http, DriveConnection connection, string? operation, string? folder);
    Task<DriveImportReceipt> ImportAsync(HttpContext http, DriveCustomer customer, DriveConnection connection,
        DriveFile file, byte[] content, Func<Task> revalidate);
}

public sealed class DriveOwners(CustomerOwnerClient identity, TenantAccessClient access, IConfiguration configuration,
    HttpClient client) : IDriveOwners, IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true };
    public async Task<DriveCustomer> AuthorizeAsync(HttpContext http)
    {
        var session = await identity.SessionAsync(http);
        var workspace = await identity.OwnedCustomerAsync(session.TenantId, session.ActorId, http.RequestAborted)
            ?? throw new TenantBoundaryException("CUSTOMER_OWNER_REQUIRED", 403);
        if (!workspace.WorkspaceReady || workspace.OwnerActorId != session.ActorId || !workspace.Active)
            throw new TenantBoundaryException("CUSTOMER_OWNER_REQUIRED", 403);
        var decision = await access.EvaluateCustomerAsync(http, "integration-gateway.connection.manage", workspace.CustomerId);
        if (decision.Outcome != "Allow" || decision.ActorId != session.ActorId || decision.AccessBasis != "Self" || decision.CustomerVersion != workspace.Version)
            throw new TenantBoundaryException("ACCESS_DENIED", 403);
        return new(session, workspace);
    }
    public async Task<DriveConsentDecision> ConsentAsync(HttpContext http, DriveConnection connection, string? operation, string? folder)
    {
        using var request = Message(http, operation is null ? HttpMethod.Get : HttpMethod.Post,
            "http://127.0.0.1:5110/local/v1/provider-consents/google-drive/" + connection.Id);
        if (operation is not null) request.Content = JsonContent.Create(new
        { connectionId = connection.Id, operation, expectedVersion = connection.ConsentVersion, folderId = folder });
        var value = await SendAsync<DriveConsentDecision>(request, http.RequestAborted);
        if (value.ConnectionId != connection.Id || value.CustomerId != connection.CustomerId || value.Provider != "google-drive" ||
            value.Purpose != "document-import" || value.Version < 0)
            throw new TenantBoundaryException("CONSENT_RESPONSE_INVALID", 503);
        return value;
    }
    public async Task<DriveImportReceipt> ImportAsync(HttpContext http, DriveCustomer customer, DriveConnection connection,
        DriveFile file, byte[] content, Func<Task> revalidate)
    {
        var operation = DriveConnectionStore.Hash(string.Join('|', connection.TenantId, connection.CustomerId, connection.Id, file.Id, file.Version))[..32];
        var hash = Convert.ToHexStringLower(SHA256.HashData(content));
        var evidenceRoot = TenantAccessIntegration.LoopbackRoot(TenantAccessIntegration.Required(configuration, "Monergy:GoogleDrive:EvidenceUrl"));
        var token = TenantAccessIntegration.Required(configuration, "Monergy:GoogleDrive:EvidenceToken");
        TenantAccessIntegration.ValidateToken(token);
        await RequireUploadAsync(http, customer);
        using var stage = new HttpRequestMessage(HttpMethod.Post, new Uri(evidenceRoot, "internal/local/v1/evidence/content"));
        stage.Headers.Add("X-Monergy-Product-Token", token);
        stage.Content = JsonContent.Create(new { operationId = operation, contentBase64 = Convert.ToBase64String(content), contentType = file.MimeType, sourceLocation = new { provider = "google-drive", folderId = connection.FolderId, fileId = file.Id, fileVersion = file.Version } });
        var descriptor = await SendAsync<DriveEvidenceDescriptor>(stage, http.RequestAborted);
        if (descriptor.Sha256 != hash || descriptor.ContentLength != content.Length ||
            !EvidenceIntakeReference.Matches(descriptor.Reference, operation))
            throw new TenantBoundaryException("EVIDENCE_RESPONSE_INVALID", 503);
        // Scanning can be slow: refresh owner, configuration and consent before
        // registration. The Evidence contract independently reauthorizes too.
        await revalidate();
        var decision = await RequireUploadAsync(http, customer);
        var now = connection.ImportTimes[file.Id + ":" + file.Version];
        var body = new ContractRequest<CreateDocumentVersion>(Vs02ContractNames.CreateDocumentVersion,
            ContractGuard.CurrentVersion, "request-" + operation, "correlation-" + operation, null,
            new TrustedSecurityContext(new ActorContext(customer.Session.ActorId, "Customer", now, customer.Session.AuthenticationContextId),
                new WorkloadContext("integration-gateway", "local-uat-workspace:" + connection.TenantId),
                new AccessContext("customer-advice", null, decision.DecisionId, connection.CustomerId)), operation,
            new CreateDocumentVersion("document-" + operation, "document-version-" + operation, "evidence-" + operation,
                connection.CustomerId, "Google Drive", file.Name, file.MimeType,
                descriptor.Reference, descriptor.Sha256, now));
        using var register = Message(http, HttpMethod.Post, new Uri(evidenceRoot, "contracts/cid-020/v1").AbsoluteUri);
        register.Content = JsonContent.Create(body, options: ContractJson.Options);
        var result = await SendAsync<ContractResult<DocumentVersionCreatedResult>>(register, http.RequestAborted, ContractJson.Options);
        if (result.Outcome != ContractOutcome.Success || result.Data is null)
            throw new TenantBoundaryException("EVIDENCE_REGISTRATION_FAILED", result.Error?.Category == ContractErrorCategory.Conflict ? 409 : 503);
        if (result.Data.DocumentId != body.Payload.DocumentId || result.Data.DocumentVersionId != body.Payload.DocumentVersionId || result.Data.ContentSha256 != hash)
            throw new TenantBoundaryException("EVIDENCE_RESPONSE_INVALID", 503);
        return new(connection.Id, file.Id, file.Version, file.Name, result.Data.DocumentId, result.Data.DocumentVersionId, hash, result.Data.CreatedAt);
    }
    private async Task<CustomerAccessDecision> RequireUploadAsync(HttpContext http, DriveCustomer customer)
    {
        var decision = await access.EvaluateCustomerAsync(http, "evidence.document.upload", customer.Workspace.CustomerId);
        if (decision.Outcome != "Allow" || decision.ActorId != customer.Session.ActorId || decision.AccessBasis != "Self" || decision.CustomerVersion != customer.Workspace.Version)
            throw new TenantBoundaryException("ACCESS_DENIED", 403);
        return decision;
    }
    private static HttpRequestMessage Message(HttpContext http, HttpMethod method, string url)
    {
        var (tenant, session) = TenantAccessClient.Headers(http);
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-Monergy-Tenant", tenant); request.Headers.Add("X-Monergy-Session", session); return request;
    }
    public void Dispose() => client.Dispose();
    private async Task<T> SendAsync<T>(HttpRequestMessage request, CancellationToken ct, JsonSerializerOptions? json = null)
    {
        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new TenantBoundaryException("DRIVE_OWNER_REQUEST_REJECTED",
            (int)response.StatusCode is 400 or 401 or 403 or 409 or 422 ? (int)response.StatusCode : 503);
        return await response.Content.ReadFromJsonAsync<T>(json ?? Json, ct) ?? throw new TenantBoundaryException("DRIVE_OWNER_RESPONSE_INVALID", 503);
    }
}

internal sealed record DriveEvidenceDescriptor(string Reference, string Sha256, long ContentLength);
