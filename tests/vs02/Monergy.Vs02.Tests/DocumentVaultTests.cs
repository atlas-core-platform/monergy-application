using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.Evidence;
using Monergy.Services.Evidence.Application;
using Monergy.Services.Evidence.Infrastructure;
using Xunit;

namespace Monergy.Vs02.Tests;

public sealed class DocumentVaultTests
{
    private static readonly VaultScope Scope = new("T001", "customer-one");
    private static EvidenceVersionRecord Row(string id = "version-one", string document = "document-one", string tenant = "T001", string customer = "customer-one") =>
        new(document, id, "evidence-" + id, customer, 0, "Customer upload", "statement.pdf", "application/pdf", "reference://fixture", new('a', 64), DateTimeOffset.UnixEpoch.AddDays(100), DateTimeOffset.UnixEpoch.AddDays(100), 10, TenantId: tenant);
    private static Task<EvidenceSaveResult> Save(InMemoryEvidenceRepository repo, EvidenceVersionRecord row) => repo.SaveVersionAsync(new("CreateDocumentVersion", "1.0.0", row.CustomerId, "op-" + row.DocumentVersionId), row, _ => ["event"], default);
    private static VaultMetadataChange Change(string op = "metadata-one", int revision = 0) => new(op, revision, "Banking", "Bank statement", "2026-04-01", "2026-2027");

    [Fact] public async Task MetadataIsVersionScopedAndDoesNotChangeEvidenceOrClassification()
    {
        var repo = new InMemoryEvidenceRepository(); var original = (await Save(repo, Row())).Version;
        var metadata = await repo.VaultSaveAsync(Scope, original.DocumentVersionId, "A100", Change(), default);
        Assert.Equal(1, metadata.Revision); Assert.Equal("A100", metadata.UpdatedBy);
        Assert.Equal(original, await repo.GetVersionAsync(original.DocumentVersionId, default));
        var view = (await repo.VaultReadAsync(Scope, original.DocumentVersionId, default))!;
        Assert.Equal("NOT_VERIFIED", view.VerificationStatus); Assert.Equal("UNRECORDED", view.ScanStatus); Assert.Equal("TEMPORARY_REFERENCE", view.StorageMode);
        await Save(repo, Row("version-two") with { ContentSha256 = new('b', 64), ExpectedPreviousVersionId = "version-one" });
        Assert.Equal(VaultMetadata.Empty, (await repo.VaultReadAsync(Scope, "version-two", default))!.Metadata);
        Assert.Equal(metadata, (await repo.VaultReadAsync(Scope, "version-one", default))!.Metadata);
    }
    [Fact] public async Task MetadataReplayReturnsOriginalReceiptAndConflictsDoNotOverwrite()
    {
        var repo = new InMemoryEvidenceRepository(); await Save(repo, Row());
        var receipt = await repo.VaultSaveAsync(Scope, "version-one", "A100", Change(), default);
        await repo.VaultSaveAsync(Scope, "version-one", "A100", Change("next", 1) with { Category = "Credit" }, default);
        Assert.Equal(receipt, await repo.VaultSaveAsync(Scope, "version-one", "A100", Change(), default));
        Assert.Equal("VAULT_OPERATION_CONFLICT", (await Assert.ThrowsAsync<TenantBoundaryException>(() => repo.VaultSaveAsync(Scope, "version-one", "A200", Change(), default))).Code);
        Assert.Equal("VAULT_METADATA_CHANGED", (await Assert.ThrowsAsync<TenantBoundaryException>(() => repo.VaultSaveAsync(Scope, "version-one", "A100", Change("stale"), default))).Code);
    }
    [Theory][InlineData("T002", "customer-one")][InlineData("T001", "other")]
    public async Task ForeignScopeCannotListReadOrEdit(string tenant, string customer)
    {
        var repo = new InMemoryEvidenceRepository(); await Save(repo, Row()); var scope = new VaultScope(tenant, customer);
        Assert.Empty((await repo.VaultListAsync(scope, "all", "", null, default)).Items);
        Assert.Null(await repo.VaultReadAsync(scope, "version-one", default));
        Assert.Equal(404, (await Assert.ThrowsAsync<TenantBoundaryException>(() => repo.VaultSaveAsync(scope, "version-one", "A100", Change(), default))).Status);
        await Assert.ThrowsAsync<TenantBoundaryException>(() => repo.VaultListAsync(scope, "all", "", "version-one", default));
    }
    [Fact] public async Task PagesReachPastOneHundredWithStableTieOrderingAndLiteralSearch()
    {
        var repo = new InMemoryEvidenceRepository();
        for (var i = 0; i < 111; i++) await Save(repo, Row("version-" + i.ToString("D3", System.Globalization.CultureInfo.InvariantCulture), "document-" + i));
        var first = await repo.VaultListAsync(Scope, "Other", "statement", null, default);
        var second = await repo.VaultListAsync(Scope, "Other", "statement", first.NextCursor, default);
        var third = await repo.VaultListAsync(Scope, "Other", "statement", second.NextCursor, default);
        Assert.Equal(111, first.Items.Concat(second.Items).Concat(third.Items).Select(x => x.DocumentVersionId).Distinct().Count()); Assert.Null(third.NextCursor);
        Assert.Empty((await repo.VaultListAsync(Scope, "all", "%", null, default)).Items);
        await repo.VaultSaveAsync(Scope, "version-000", "A100", Change(), default);
        Assert.Equal("version-000", Assert.Single((await repo.VaultListAsync(Scope, "Banking", "", null, default)).Items).DocumentVersionId);
    }
    [Fact] public async Task ReplacementIsAtomicReplayableAndRejectsStaleOrIdenticalContent()
    {
        var repo = new InMemoryEvidenceRepository(); var original = (await Save(repo, Row())).Version; repo.DrainOutbox();
        var candidate = Row("version-two") with { ContentSha256 = new('b', 64), ExpectedPreviousVersionId = "version-one" };
        Assert.True((await Save(repo, candidate)).Created); Assert.False((await Save(repo, candidate)).Created); Assert.Single(repo.DrainOutbox());
        Assert.Equal(original, await repo.GetVersionAsync("version-one", default));
        Assert.Equal("VAULT_VERSION_CHANGED", (await Assert.ThrowsAsync<TenantBoundaryException>(() => Save(repo, candidate with { DocumentVersionId = "stale" }))).Code);
        Assert.Equal("VAULT_CONTENT_UNCHANGED", (await Assert.ThrowsAsync<TenantBoundaryException>(() => Save(repo, candidate with { DocumentVersionId = "same", ExpectedPreviousVersionId = "version-two" }))).Code);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Save(repo, candidate with { OriginalFileName = "changed.pdf" }));
        Assert.Equal(2, (await repo.GetDocumentAsync("document-one", default)).Count); Assert.Empty(repo.DrainOutbox());
    }
    [Theory][InlineData("Other", "2026-2028", "2026-04-01")][InlineData("Bad", null, null)][InlineData("Other", null, "2026-02-30")]
    public void InvalidOrganizationMetadataIsRejected(string category, string? year, string? date) =>
        Assert.Throws<TenantBoundaryException>(() => DocumentVault.Validate(Change() with { Category = category, FinancialYear = year, DocumentDate = date }));
    [Fact] public async Task OwnerReplacementRunsVerifiedIntakeAndEmitsOnlyNewVersionEvent()
    {
        var repo = new InMemoryEvidenceRepository(); await Save(repo, Row()); repo.DrainOutbox();
        var content = new ReferenceEvidenceContentStore(); var bytes = "replacement"u8.ToArray(); var check = FixtureMalwareScanner.Check(bytes);
        var staged = content.StageScanned("replacement-one", bytes, "text/plain", check);
        var app = new EvidenceApplication(repo, content, new CapturingTelemetry(), TimeProvider.System);
        var request = Vs02TestContext.Request(Vs02ContractNames.CreateDocumentVersion,
            new CreateDocumentVersion("document-one", "version-two", "evidence-two", "customer-one", "Customer replacement", "statement.txt", "text/plain", staged.Reference, staged.Sha256, DateTimeOffset.UtcNow),
            "request-replacement", "replacement-one", Vs02TestContext.Security("customer-one") with { Workload = new("customer-web", "local-uat-workspace:T001") });
        Assert.Equal(ContractOutcome.Success, (await app.CreateDocumentVersionAsync(request, "version-one")).Outcome);
        Assert.Equal(ContractOutcome.Success, (await app.CreateDocumentVersionAsync(request, "version-one")).Outcome);
        Assert.Single(repo.DrainOutbox()); Assert.Equal(check, (await repo.GetVersionAsync("version-two", default))!.MalwareCheck);
        Assert.Equal("VERIFIED_AT_INTAKE", (await repo.VaultReadAsync(Scope, "version-two", default))!.ScanStatus);
    }
    [Fact] public async Task CloudSourceLocationIsImmutableAndAvailableWithoutExposingCredentials()
    {
        var repo = new InMemoryEvidenceRepository(); var content = new ReferenceEvidenceContentStore(); var bytes = "cloud evidence"u8.ToArray();
        var location = new EvidenceSourceLocation("google-drive", "folder-one", "file-one", "provider-version-one");
        var descriptor = content.StageScanned(new string('c', 32), bytes, "text/plain", FixtureMalwareScanner.Check(bytes), location);
        Assert.Equal(location, descriptor.SourceLocation);
        Assert.Throws<InvalidOperationException>(() => content.StageScanned(new string('c', 32), bytes, "text/plain", FixtureMalwareScanner.Check(bytes), location with { FileId = "other" }));
        var owner = new EvidenceApplication(repo, content, new CapturingTelemetry(), TimeProvider.System);
        var request = Vs02TestContext.Request(Vs02ContractNames.CreateDocumentVersion,
            new CreateDocumentVersion("document-one", "version-one", "evidence-one", "customer-one", "Google Drive", "cloud.txt", "text/plain", descriptor.Reference, descriptor.Sha256, DateTimeOffset.UtcNow),
            "request-cloud", "cloud-one", Vs02TestContext.Security("customer-one") with { Workload = new("integration-gateway", "local-uat-workspace:T001") });
        Assert.Equal(ContractOutcome.Success, (await owner.CreateDocumentVersionAsync(request)).Outcome);
        Assert.Equal(location, (await repo.VaultReadAsync(Scope, "version-one", default))!.SourceLocation);
    }
    [Fact] public async Task HttpRequiresSelfCurrentAuthorityAndRejectsCrossOriginAndForeignTenant()
    {
        await using var host = await Host.Start();
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/local/v1/customers/customer-one/document-vault")).StatusCode);
        host.Authority.Basis = "Advisor"; Assert.Equal(HttpStatusCode.Forbidden, (await host.Client.GetAsync("/local/v1/customers/customer-one/document-vault")).StatusCode);
        host.Authority.Basis = "Self"; host.Authority.Calls = 0; host.Authority.DenyAfter = 1;
        Assert.Equal(HttpStatusCode.Forbidden, (await host.Client.GetAsync("/local/v1/customers/customer-one/document-vault/version-one")).StatusCode);
        host.Authority.DenyAfter = int.MaxValue; host.Client.DefaultRequestHeaders.Add("Origin", "https://foreign.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await host.Client.PutAsJsonAsync("/local/v1/customers/customer-one/document-vault/version-one/metadata", Change())).StatusCode);
        host.Client.DefaultRequestHeaders.Remove("Origin");
        Assert.Equal(HttpStatusCode.Forbidden, (await host.Client.GetAsync("/local/v1/customers/foreign-customer/document-vault")).StatusCode);
    }
    [Fact] public async Task HttpRechecksReadAndUploadBeforeMetadataCommit()
    {
        await using var host = await Host.Start(); host.Authority.DenyAfter = 3;
        Assert.Equal(HttpStatusCode.Forbidden, (await host.Client.PutAsJsonAsync("/local/v1/customers/customer-one/document-vault/version-one/metadata", Change())).StatusCode);
        Assert.Equal(0, (await host.Repo.VaultReadAsync(Scope, "version-one", default))!.Metadata.Revision);
        host.Authority.DenyAfter = int.MaxValue;
        Assert.Equal(HttpStatusCode.OK, (await host.Client.PutAsJsonAsync("/local/v1/customers/customer-one/document-vault/version-one/metadata", Change())).StatusCode);
    }
    [Fact] public async Task KycEvidenceRequiresCurrentDelegatedAuthorityImmutableHashAndOriginalTenant()
    {
        await using var host = await Host.Start();
        var bytes = "%PDF-1.7 fixture"u8.ToArray();var check=FixtureMalwareScanner.Check(bytes);
        var staged=host.Content.StageScanned("kyc-content",bytes,"application/pdf",check);
        await Save(host.Repo,Row("kyc-version","kyc-document") with {ContentReference=staged.Reference,ContentSha256=staged.Sha256,MalwareCheck=check});
        host.Authority.Basis="Advisor";
        var response=await host.Client.GetAsync("/local/v1/customers/customer-one/kyc-evidence/kyc-version");Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        var data=await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();Assert.Equal(Convert.ToBase64String(bytes),data.GetProperty("contentBase64").GetString());Assert.False(data.TryGetProperty("contentReference",out _));
        host.Authority.DenyAfter=host.Authority.Calls+1;
        Assert.Equal(HttpStatusCode.Forbidden,(await host.Client.GetAsync("/local/v1/customers/customer-one/kyc-evidence/kyc-version")).StatusCode);
        host.Authority.DenyAfter=int.MaxValue;
        Assert.Equal(HttpStatusCode.Forbidden,(await host.Client.GetAsync("/local/v1/customers/foreign-customer/kyc-evidence/kyc-version")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,(await host.Client.GetAsync("/local/v1/customers/customer-one/kyc-evidence/missing")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,(await host.Client.GetAsync("/local/v1/customers/customer-one/kyc-evidence/version-one")).StatusCode);
        await Save(host.Repo,Row("wrong-hash","hash-document") with {ContentReference=staged.Reference,MalwareCheck=check});
        Assert.Equal(HttpStatusCode.ServiceUnavailable,(await host.Client.GetAsync("/local/v1/customers/customer-one/kyc-evidence/wrong-hash")).StatusCode);
    }
    private sealed class Authority : HttpMessageHandler
    {
        public string Basis = "Self"; public int Calls, DenyAfter = int.MaxValue;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++; var body = (await request.Content!.ReadFromJsonAsync<CustomerAccessRequest>(cancellationToken: ct))!;
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new CustomerAccessDecision("decision-one", Calls > DenyAfter ? "Deny" : "Allow", "T001", "A100", body.CustomerId, body.CapabilityId, "customer-advice", "Granted", Basis, 1, 1, 1, 1, DateTimeOffset.UtcNow)) };
        }
    }
    private sealed class Host(WebApplication app, HttpClient client, Authority authority, InMemoryEvidenceRepository repo, ReferenceEvidenceContentStore content) : IAsyncDisposable
    {
        public ReferenceEvidenceContentStore Content => content; public HttpClient Client => client; public Authority Authority => authority; public InMemoryEvidenceRepository Repo => repo;
        public static async Task<Host> Start()
        {
            var repo = new InMemoryEvidenceRepository(); await Save(repo, Row()); var authority = new Authority();
            var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Monergy:ExecutionZone"] = "CI_EPHEMERAL" }).Build();
            builder.Services.AddSingleton(new TenantAccessClient(config, new HttpClient(authority)));
            builder.Services.AddSingleton(new TenantBoundaryOptions { Service = "evidence", CapabilityPrefix = "evidence.", ProbeUrl = "http://127.0.0.1:5111/", CustomerTenants = new Dictionary<string, string> { ["customer-one"] = "T001", ["foreign-customer"] = "T002" } });
            builder.Services.AddSingleton<IDocumentVaultRepository>(repo); builder.Services.AddSingleton<IEvidenceRepository>(repo);
            var content = new ReferenceEvidenceContentStore(); builder.Services.AddSingleton(content);
            builder.Services.AddSingleton(new EvidenceApplication(repo, content, new CapturingTelemetry(), TimeProvider.System));
            var app = builder.Build(); app.MapDocumentVault(); app.MapKycEvidence(); await app.StartAsync();
            var url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single(); var client = new HttpClient { BaseAddress = new Uri(url) };
            client.DefaultRequestHeaders.Add("X-Monergy-Tenant", "T001"); client.DefaultRequestHeaders.Add("X-Monergy-Session", "session-one"); return new(app, client, authority, repo, content);
        }
        public async ValueTask DisposeAsync() { client.Dispose(); await app.DisposeAsync(); }
    }
}
