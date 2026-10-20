using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.FinancialProfile;
using Monergy.Services.FinancialProfile.Application;
using Monergy.Services.FinancialProfile.Infrastructure;
using Monergy.UatGateway;
using Xunit;
namespace Monergy.FinancialProfile.Tests;

public sealed partial class ReviewedTaxRecordTests
{
    private const string Customer = FinancialProfileTestContext.CustomerId;
    private static CreateTaxRecord Input(string source,int revision=1) => new(new string('a',32),
        new("Tax statement","India","Income tax",400000m,"PAID","2026-27",new(2026,9,1),new(2026,9,30),source,revision));
    [Theory,InlineData("operation"),InlineData("upper-operation"),InlineData("name"),InlineData("payer"),InlineData("type"),InlineData("control"),
        InlineData("amount"),InlineData("precision"),InlineData("large"),InlineData("amountKind"),InlineData("year"),InlineData("long-year"),InlineData("early"),InlineData("order"),InlineData("future"),InlineData("source"),InlineData("revision")]
    public void InvalidInputIsRejectedBeforeSourceOrStorageAccess(string field)
    {
        var input=Input("ff-source");var draft=input.Tax;
        input=field switch {
            "operation"=>input with {OperationId="invalid"}, "upper-operation"=>input with {OperationId=new string('A',32)},
            "name"=>input with {Tax=draft with {Name=" "}},"payer"=>input with {Tax=draft with {Jurisdiction=new string('x',121)}},
            "type"=>input with {Tax=draft with {TaxType=""}},"control"=>input with {Tax=draft with {Jurisdiction="a\nb"}},
            "amount"=>input with {Tax=draft with {Amount=-1}},"precision"=>input with {Tax=draft with {Amount=1.001m}},
            "large"=>input with {Tax=draft with {Amount=1000000000000000m}},"amountKind"=>input with {Tax=draft with {AmountKind="GUESS"}},
            "year"=>input with {Tax=draft with {TaxYearLabel=" "}},"long-year"=>input with {Tax=draft with {TaxYearLabel=new string('x',31)}},
            "early"=>input with {Tax=draft with {PeriodStart=new(1899,1,1)}},"order"=>input with {Tax=draft with {PeriodEnd=new(2026,8,31)}},
            "future"=>input with {Tax=draft with {PeriodEnd=new(2099,1,1)}},"source"=>input with {Tax=draft with {SourceFinancialFactId="bad source"}},
            _=>input with {Tax=draft with {SourceRevision=0}}
        };
        Assert.False(TaxRecordsApplication.Valid(input,DateTimeOffset.UtcNow));Assert.False(TaxRecordsApplication.Valid(null,DateTimeOffset.UtcNow));
    }
    [Fact]
    public void CurrentLocalFinancialDateIsValidBeforeUtcMidnightButFutureDatesAreNot()
    {
        var now = new DateTimeOffset(2026,10,18,20,0,0,TimeSpan.Zero);
        var input = Input("ff-source");
        Assert.True(TaxRecordsApplication.Valid(input with { Tax = input.Tax with { PeriodEnd = new(2026,10,19) } }, now));
        Assert.False(TaxRecordsApplication.Valid(input with { Tax = input.Tax with { PeriodEnd = new(2026,10,20) } }, now));
    }
    [Fact]
    public async Task ExactHistoricalSourceAndOriginalAmountsSurviveRecordCreationAndReplay()
    {
        var (financial, source) = await Seed(); var repository = new InMemoryTaxRecords();
        var app = new TaxRecordsApplication(financial, repository, TimeProvider.System);
        var newSource = new ValidatedSourceFact("source-tax-2", Customer, "TAX_RECORD", "Amount stated in source", 110000m, "USD", new(2026,2,1), "line 2", 1m, "evidence-v2", "document-v2", "fixture-extractor/1.0.0", "validation/1.0.0");
        var financialApp = new FinancialProfileApplication(financial, new CapturingTelemetry(), TimeProvider.System);
        await financialApp.NormalizeSourceFactsAsync(FinancialProfileTestContext.Request(Vs02ContractNames.NormalizeSourceFacts,
            new NormalizeSourceFacts("profile-"+Customer, Customer, "processing-2", [newSource], "normalize/1.0.0", DateTimeOffset.UtcNow), "new-source", "new-source"));
        var request = Input(source.FinancialFactId);
        var first = await app.CreateAsync("T001", Customer, "actor-001", request, default);
        var replay = await app.CreateAsync("T001", Customer, "actor-001", request, default);
        Assert.Equal(first, replay); Assert.Equal(400000m, first.Tax.Amount);
        Assert.Equal("USD", first.Currency); Assert.Equal("actor-001", first.RecordedBy);Assert.Equal("CUSTOMER_REVIEWED",first.VerificationStatus);
        Assert.Equal(source.FinancialProvenanceId, first.SourceProvenanceId);
        Assert.Equal("document-v1", first.DocumentVersionId); Assert.Equal("evidence-v1", first.EvidenceId);
        Assert.Equal(100000m, (await financial.GetFactAsync(source.FinancialFactId, 1, default))!.Value);
        Assert.Equal(110000m, (await financial.GetFactAsync(source.FinancialFactId, null, default))!.Value);
        Assert.Equal(first, await app.ReadAsync("T001", Customer, first.TaxRecordId, default));
        Assert.Single((await app.ListAsync("T001", Customer, default)).Items);
        Assert.Empty((await app.ListAsync("T002", Customer, default)).Items);
        Assert.Equal(404, (await Assert.ThrowsAsync<TenantBoundaryException>(() => app.ReadAsync("T002", Customer, first.TaxRecordId, default))).Status);
        Assert.Equal(404, (await Assert.ThrowsAsync<TenantBoundaryException>(() => app.ReadAsync("T001", "other", first.TaxRecordId, default))).Status);
    }
    [Fact]
    public async Task ChangedOperationPayloadConflictsWithoutChangingTheCreationReceipt()
    {
        var (financial, source) = await Seed(); var app = new TaxRecordsApplication(financial, new InMemoryTaxRecords(), TimeProvider.System);
        var input = Input(source.FinancialFactId); var first = await app.CreateAsync("T001", Customer, "actor-001", input, default);
        var error = await Assert.ThrowsAsync<TenantBoundaryException>(() => app.CreateAsync("T001", Customer, "actor-001", input with { Tax = input.Tax with { Amount = 390000 } }, default));
        Assert.Equal(409, error.Status); Assert.Equal(first, await app.ReadAsync("T001", Customer, first.TaxRecordId, default));
        Assert.Single((await app.ListAsync("T001", Customer, default)).Items);
    }
    [Fact]
    public async Task CompetingPayloadsWithOneOperationCreateOnlyOneImmutableRecord()
    {
        var (financial, source) = await Seed(); var app = new TaxRecordsApplication(financial, new InMemoryTaxRecords(), TimeProvider.System);
        var input = Input(source.FinancialFactId);
        async Task<bool> Attempt(decimal outstanding) { try { await app.CreateAsync("T001", Customer, "actor-001", input with { Tax = input.Tax with { Amount = outstanding } }, default); return true; } catch (TenantBoundaryException e) when (e.Status == 409) { return false; } }
        var results = await Task.WhenAll(Task.Run(() => Attempt(1)), Task.Run(() => Attempt(2)));
        Assert.Equal(1, results.Count(x => x)); Assert.Single((await app.ListAsync("T001", Customer, default)).Items);
    }
    [Theory, InlineData("EXPENSE", Customer), InlineData("TAX_RECORD", "other-customer")]
    public async Task WrongCategoryOrForeignSourceCannotCreateARecord(string type, string customer)
    {
        var (financial, source) = await Seed(type, customer); var repository = new InMemoryTaxRecords();
        var app = new TaxRecordsApplication(financial, repository, TimeProvider.System);
        Assert.Equal(404, (await Assert.ThrowsAsync<TenantBoundaryException>(() => app.CreateAsync("T001", Customer, "actor-001", Input(source.FinancialFactId), default))).Status);
        Assert.Empty((await repository.ListAsync("T001", Customer, default)).Items);
    }
    [Fact]
    public async Task MissingSourceRevisionCreatesNothing()
    {
        var (financial, source) = await Seed(); var app = new TaxRecordsApplication(financial, new InMemoryTaxRecords(), TimeProvider.System);
        Assert.Equal(404, (await Assert.ThrowsAsync<TenantBoundaryException>(() => app.CreateAsync("T001", Customer, "actor-001", Input(source.FinancialFactId, 2), default))).Status);
    }
    [Fact]
    public async Task ActualOwnerAndGatewayRoutesCreateReadbackAndReplayOneSourceLinkedRecord()
    {
        await using var h = await Harness.Create(); using var client = h.Client(); var input = Input(h.Source.FinancialFactId);
        var created = await client.PostAsJsonAsync("/product-api/v1/tax-records", input, ContractJson.Options); Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var first = await created.Content.ReadFromJsonAsync<ReviewedTaxRecord>(ContractJson.Options); Assert.NotNull(first);
        Assert.Equal(input.Tax, first.Tax); Assert.Equal("actor-001", first.RecordedBy);Assert.Equal("CUSTOMER_REVIEWED",first.VerificationStatus);
        Assert.Equal("no-store", created.Headers.CacheControl?.ToString());
        var replay = await client.PostAsJsonAsync("/product-api/v1/tax-records", input, ContractJson.Options); Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(first, await replay.Content.ReadFromJsonAsync<ReviewedTaxRecord>(ContractJson.Options));
        var list = await client.GetFromJsonAsync<ProductTaxRecordList>("/product-api/v1/tax-records", ContractJson.Options);
        Assert.Equal(first, Assert.Single(list!.Items)); Assert.False(list.HasMore);
    }
    [Theory, InlineData("missing", 401), InlineData("deny", 403), InlineData("advisor", 403), InlineData("unavailable", 503), InlineData("tenant",401), InlineData("update",403), InlineData("evidence",403), InlineData("read",403)]
    public async Task CurrentAuthorityFailureCreatesNothing(string failure, int status)
    {
        await using var h = await Harness.Create(); h.Authority.Failure = failure; using var client = h.Client(failure == "missing" ? null : failure == "tenant" ? "T002" : "T001");
        var response = await client.PostAsJsonAsync("/product-api/v1/tax-records", Input(h.Source.FinancialFactId), ContractJson.Options);
        Assert.Equal(status, (int)response.StatusCode); Assert.Empty((await h.Records.ListAsync("T001", Customer, default)).Items);
    }
    [Theory, InlineData("verificationStatus"), InlineData("currency"), InlineData("customerId"), InlineData("recordedBy"), InlineData("duplicate")]
    public async Task ActualOwnerRejectsExtraAuthorityFieldsAndDuplicateJson(string field)
    {
        await using var h = await Harness.Create(); using var client = h.OwnerClient(); var json = JsonSerializer.Serialize(Input(h.Source.FinancialFactId), ContractJson.Options);
        json = field == "duplicate" ? json.Insert(1, "\"operationId\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",") : json.Insert(1, "\"" + field + "\":\"forged\",");
        using var body = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/local/v1/customers/" + Customer + "/tax-records", body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.Empty((await h.Records.ListAsync("T001", Customer, default)).Items);
    }
    [Fact]
    public async Task LostConfirmationAfterCommitReplaysTheOriginalRecordAfterAuthorityReturns()
    {
        await using var h = await Harness.Create(); using var client = h.Client(); var input = Input(h.Source.FinancialFactId);
        h.Authority.DenyAfterEvaluations = 6;
        var unconfirmed = await client.PostAsJsonAsync("/product-api/v1/tax-records",input,ContractJson.Options);
        Assert.Equal(HttpStatusCode.Forbidden,unconfirmed.StatusCode);
        var stored=Assert.Single((await h.Records.ListAsync("T001",Customer,default)).Items);
        h.Authority.DenyAfterEvaluations = 0;
        var confirmed=await client.PostAsJsonAsync("/product-api/v1/tax-records",input,ContractJson.Options);
        Assert.Equal(HttpStatusCode.OK,confirmed.StatusCode);
        Assert.Equal(stored,await confirmed.Content.ReadFromJsonAsync<ReviewedTaxRecord>(ContractJson.Options));
        Assert.Single((await h.Records.ListAsync("T001",Customer,default)).Items);
    }
    [Fact]
    public async Task OwnerBindingRejectsForeignTenantEvenWhenPdpAllowsACollidingCustomerId()
    {
        await using var h = await Harness.Create();
        using(var original = h.Client())
            Assert.Equal(HttpStatusCode.OK,(await original.PostAsJsonAsync("/product-api/v1/tax-records",Input(h.Source.FinancialFactId),ContractJson.Options)).StatusCode);
        var saved = Assert.Single((await h.Records.ListAsync("T001",Customer,default)).Items);
        h.Authority.AllowForeignTenant = true;
        using var foreign = h.OwnerClient("T002");
        var path = "/local/v1/customers/"+Customer+"/tax-records";
        Assert.Equal(HttpStatusCode.Forbidden,(await foreign.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await foreign.GetAsync(path+"/"+saved.TaxRecordId)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await foreign.PostAsJsonAsync(path,Input(h.Source.FinancialFactId),ContractJson.Options)).StatusCode);
        Assert.Empty((await h.Records.ListAsync("T002",Customer,default)).Items);
        Assert.Single((await h.Records.ListAsync("T001",Customer,default)).Items);
    }
    [Theory, InlineData("customerId"),InlineData("recordedBy"),InlineData("currency"),InlineData("operationId"),InlineData("source"),InlineData("amount"),InlineData("verificationStatus"),InlineData("null"),InlineData("readback")]
    public async Task GatewayRejectsUnboundOrUnconfirmedOwnerReceiptsAfterARealWrite(string tamper)
    {
        await using var h=await Harness.Create(tamper); using var client=h.Client();
        var response=await client.PostAsJsonAsync("/product-api/v1/tax-records",Input(h.Source.FinancialFactId),ContractJson.Options);
        Assert.Equal(HttpStatusCode.ServiceUnavailable,response.StatusCode);
        Assert.Single((await h.Records.ListAsync("T001",Customer,default)).Items);
    }
    [Theory, InlineData("unknown"),InlineData("nested"),InlineData("duplicate"),InlineData("missing"),InlineData("null"),InlineData("date"),InlineData("large")]
    public async Task GatewayStrictInputRejectsAmbiguousBodiesBeforeMutation(string variation)
    {
        await using var h=await Harness.Create(); using var client=h.Client();
        var json=JsonNode.Parse(JsonSerializer.Serialize(Input(h.Source.FinancialFactId),ContractJson.Options))!.AsObject();
        switch(variation) {
            case "unknown":json["currency"]="INR";break;
            case "nested":json["tax"]!["customerId"]="foreign";break;
            case "missing":json.Remove("operationId");break;
            case "null":json["tax"]=null;break;
            case "date":json["tax"]!["periodEnd"]="invalid";break;
            case "large":json["tax"]!["name"]=new string('x',40000);break;
        }
        var body=json.ToJsonString();if(variation=="duplicate") body=body.Insert(1,"\"operationId\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",");
        var response=await client.PostAsync("/product-api/v1/tax-records",new StringContent(body,Encoding.UTF8,"application/json"));
        Assert.Equal(variation=="large"?413:400,(int)response.StatusCode);Assert.Empty((await h.Records.ListAsync("T001",Customer,default)).Items);
    }
    [Fact]
    public async Task ExplicitZeroValuesAreAllowedAndTrimmedReviewReplaysWithoutChangingSourceValue()
    {
        var (financial,source)=await Seed();var repository=new InMemoryTaxRecords();var app=new TaxRecordsApplication(financial,repository,TimeProvider.System);
        var input=Input(source.FinancialFactId);input=input with {Tax=input.Tax with {Name=" Tax statement ",Jurisdiction=" India ",TaxType=" Bonus ",Amount=0}};
        var first=await app.CreateAsync("T001",Customer,"actor-001",input,default);
        Assert.Equal("Tax statement",first.Tax.Name);Assert.Equal("India",first.Tax.Jurisdiction);Assert.Equal("Bonus",first.Tax.TaxType);
        Assert.Equal(0,first.Tax.Amount);
        Assert.Equal(first,await app.CreateAsync("T001",Customer,"actor-001",input with {Tax=first.Tax},default));
        Assert.Equal(100000m,(await financial.GetFactAsync(source.FinancialFactId,1,default))!.Value);
    }
    [Fact]
    public async Task ListsExposeTheBoundWithoutImplyingACompletePortfolioAndOperationScopesRemainIndependent()
    {
        var (financial,source)=await Seed();var repository=new InMemoryTaxRecords();var app=new TaxRecordsApplication(financial,repository,TimeProvider.System);
        var input=Input(source.FinancialFactId);
        var one=await app.CreateAsync("T001",Customer,"actor-001",input,default);
        var other=await app.CreateAsync("T001",Customer,"actor-002",input,default);Assert.NotEqual(one.TaxRecordId,other.TaxRecordId);
        for(var i=0;i<199;i++) await app.CreateAsync("T001",Customer,"actor-001",input with {OperationId=Guid.NewGuid().ToString("N")},default);
        var list=await app.ListAsync("T001",Customer,default);Assert.Equal(200,list.Items.Count);Assert.True(list.HasMore);
    }
    private sealed class TamperHandler(string scenario) : DelegatingHandler(new SocketsHttpHandler {AllowAutoRedirect=false,UseProxy=false})
    {
        private bool corrected;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            var variation=scenario;
            var response=await base.SendAsync(request,ct);
            if(scenario.StartsWith("history-",StringComparison.Ordinal))
            {
                if(!response.IsSuccessStatusCode || request.Method!=HttpMethod.Get || !request.RequestUri!.AbsolutePath.EndsWith("/history",StringComparison.Ordinal)) return response;
                var history=JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!.AsObject();var items=history["items"]!.AsArray();
                switch(scenario) {
                    case "history-currency":items[0]!["currency"]="INR";break;
                    case "history-source":items[0]!["tax"]!["sourceRevision"]=99;break;
                    case "history-evidence":items[0]!["evidenceId"]="other-evidence";break;
                    case "history-null":history["items"]=null;break;
                    case "history-gap":items[0]!["revision"]=99;break;
                    case "history-empty":history["items"]=new JsonArray();break;
                    case "history-cursor":history["nextBeforeRevision"]=2;break;
                    case "history-tail":items.RemoveAt(items.Count-1);break;
                }
                response.Content.Dispose();response.Content=new StringContent(history.ToJsonString(),Encoding.UTF8,"application/json");return response;
            }
            if(scenario.StartsWith("list-",StringComparison.Ordinal))
            {
                if(!response.IsSuccessStatusCode || request.Method!=HttpMethod.Get || !request.RequestUri!.AbsolutePath.EndsWith("/tax-records",StringComparison.Ordinal)) return response;
                var list=JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!.AsObject();
                var items=list["items"]!.AsArray();
                switch(scenario) {
                    case "list-null":list["items"]=null;break;
                    case "list-duplicate":items.Add(items[0]!.DeepClone());break;
                    case "list-customer":items[0]!["customerId"]="foreign";break;
                    case "list-hasMore":list["hasMore"]=true;break;
                    case "list-large":while(items.Count<201) items.Add(items[0]!.DeepClone());break;
                }
                response.Content.Dispose();response.Content=new StringContent(list.ToJsonString(),Encoding.UTF8,"application/json");return response;
            }
            if(scenario.StartsWith("correction-",StringComparison.Ordinal))
            {
                variation=scenario[11..];
                if(request.Method==HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/corrections",StringComparison.Ordinal)) corrected=true;
                else if(!corrected || variation!="readback") return response;
            }
            if(!response.IsSuccessStatusCode || (variation=="readback") != (request.Method==HttpMethod.Get)) return response;
            var json=JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!.AsObject();
            switch(variation) {
                case "customerId":json["customerId"]="foreign";break;
                case "recordedBy":json["recordedBy"]="foreign";break;
                case "currency":json["currency"]="INR";break;
                case "verificationStatus":json["verificationStatus"]="VERIFIED";break;
                case "revision":json["revision"]=99;break;
                case "operationId":json["operationId"]=new string('b',32);break;
                case "source":json["tax"]!["sourceRevision"]=99;break;
                case "null":json["tax"]=null;break;
                default:json["tax"]!["amount"]=1;break;
            }
            response.Content.Dispose();response.Content=new StringContent(json.ToJsonString(),Encoding.UTF8,"application/json");return response;
        }
    }
    [Theory,InlineData("PAID"),InlineData("DUE"),InlineData("WITHHELD")]
    public async Task PaymentAmountKindAndReportedYearDoNotConvertTheExplicitPeriodTotal(string amountKind)
    {
        var (financial,source)=await Seed();var app=new TaxRecordsApplication(financial,new InMemoryTaxRecords(),TimeProvider.System);
        var input=Input(source.FinancialFactId);input=input with {Tax=input.Tax with {AmountKind=amountKind,TaxYearLabel=" Year ended September 2026 ",Amount=1234.56m}};
        var saved=await app.CreateAsync("T001",Customer,"actor-001",input,default);
        Assert.Equal(amountKind,saved.Tax.AmountKind);Assert.Equal("Year ended September 2026",saved.Tax.TaxYearLabel);Assert.Equal(1234.56m,saved.Tax.Amount);
        Assert.Equal(new DateOnly(2026,9,1),saved.Tax.PeriodStart);Assert.Equal(new DateOnly(2026,9,30),saved.Tax.PeriodEnd);Assert.Equal("CUSTOMER_REVIEWED",saved.VerificationStatus);
    }
    [Theory,InlineData("999999999999.99",true),InlineData("1000000000000",false)]
    public async Task AmountBoundPreservesCentPrecisionAndRejectsOutOfRangeRecords(string amount,bool allowed)
    {
        var (financial,source)=await Seed();var repository=new InMemoryTaxRecords();var app=new TaxRecordsApplication(financial,repository,TimeProvider.System);
        var input=Input(source.FinancialFactId);input=input with {Tax=input.Tax with {Amount=decimal.Parse(amount,System.Globalization.CultureInfo.InvariantCulture)}};
        if(allowed)
        {
            var saved=await app.CreateAsync("T001",Customer,"actor-001",input,default);
            Assert.Equal(input.Tax.Amount,saved.Tax.Amount);Assert.Equal(saved,await app.ReadAsync("T001",Customer,saved.TaxRecordId,default));
        }
        else
        {
            Assert.Equal(400,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>app.CreateAsync("T001",Customer,"actor-001",input,default))).Status);
            Assert.Empty((await repository.ListAsync("T001",Customer,default)).Items);
        }
    }
    private static async Task<(InMemoryFinancialProfileRepository Financial, AuthoritativeFinancialFact Source)> Seed(string type = "TAX_RECORD", string customer = Customer)
    {
        var repository = new InMemoryFinancialProfileRepository(); var app = new FinancialProfileApplication(repository, new CapturingTelemetry(), TimeProvider.System);
        var fact = new ValidatedSourceFact("source-tax", customer, type, "Amount stated in source", 100000m, "USD", new(2026,1,1), "line 1", 1m, "evidence-v1", "document-v1", "fixture-extractor/1.0.0", "validation/1.0.0");
        var normalized = await app.NormalizeSourceFactsAsync(FinancialProfileTestContext.Request(Vs02ContractNames.NormalizeSourceFacts,
            new NormalizeSourceFacts("profile-"+customer, customer, "processing", [fact], "normalize/1.0.0", DateTimeOffset.UtcNow), "request-seed", "key-seed", FinancialProfileTestContext.Security(customer)));
        var id = normalized.Data!.Facts.Single().FinancialFactId;
        var source = await app.GetFinancialFactAsync(FinancialProfileTestContext.Request(Vs02ContractNames.GetFinancialFact, new GetFinancialFact(id,customer,1), "read-source", security:FinancialProfileTestContext.Security(customer)));
        return (repository, source.Data!);
    }
    private sealed class Authority : HttpMessageHandler
    {
        public string Failure { get; set; } = "";
        public int DenyAfterEvaluations { get; set; }
        public bool AllowForeignTenant { get; set; }
        private int evaluations;
        public void ResetEvaluations() => evaluations = 0;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Failure == "unavailable") return new(HttpStatusCode.ServiceUnavailable);
            if (request.RequestUri!.AbsolutePath == "/v1/authorization/customer-evaluations")
            {
                var input = await request.Content!.ReadFromJsonAsync<CustomerAccessRequest>(cancellationToken:ct);
                var tenant = request.Headers.GetValues("X-Monergy-Tenant").Single();
                var denied = DenyAfterEvaluations > 0 && ++evaluations > DenyAfterEvaluations || Failure == "deny" || !AllowForeignTenant && tenant != "T001" || input!.CustomerId != Customer ||
                    Failure == "update" && input.CapabilityId.EndsWith("update", StringComparison.Ordinal) ||
                    Failure == "evidence" && input.CapabilityId == "evidence.document.read" || Failure == "read" && input.CapabilityId == "financial-profile.profile.read";
                return Response(new CustomerAccessDecision("decision-001", denied ? "Deny" : "Allow", tenant, "actor-001", input!.CustomerId,
                    input.CapabilityId, input.Purpose, denied ? "Denied" : "Granted", Failure == "advisor" ? "Advisor" : "Self", 1,2,1,1,DateTimeOffset.UtcNow));
            }
            if (request.RequestUri.AbsolutePath == "/internal/v1/tenant-sessions/validate")
                return Response(new CustomerOwnerSession("T001","actor-001","session-001",2,DateTimeOffset.UtcNow.AddHours(1)));
            if (request.RequestUri.AbsolutePath == "/internal/v1/customer-owner/actor-001")
                return Response(new CustomerOwnerWorkspace("T001",Customer,"Customer",null,"actor-001",1,true));
            throw new InvalidOperationException("Unexpected authority route.");
        }
        private static HttpResponseMessage Response<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    }
    private sealed class Harness(WebApplication owner, WebApplication gateway, Authority authority, InMemoryTaxRecords records, AuthoritativeFinancialFact source) : IAsyncDisposable
    {
        public Authority Authority => authority; public InMemoryTaxRecords Records => records; public AuthoritativeFinancialFact Source => source;
        public static async Task<Harness> Create(string? tamper=null)
        {
            var (financial, source) = await Seed(); var records = new InMemoryTaxRecords(); var authority = new Authority();
            var values = new Dictionary<string,string?> { ["Monergy:ExecutionZone"]="CI_EPHEMERAL", ["Monergy:AccessIntegration:Enabled"]="true", ["Monergy:Product:Enabled"]="true",
                ["Monergy:AccessIntegration:CustomerIdentityUrl"]="http://127.0.0.1:5001/", ["Monergy:AccessIntegration:AccessManagementUrl"]="http://127.0.0.1:5002/",
                ["Monergy:AccessIntegration:CustomerContextToken"]=new('a',32), ["Monergy:AccessIntegration:CustomerIdentityToken"]=new('b',32), ["Monergy:AccessIntegration:MembershipToken"]=new('c',32) };
            var ob = WebApplication.CreateSlimBuilder(); ob.WebHost.UseUrls("http://127.0.0.1:0"); ob.Configuration.AddInMemoryCollection(values);
            ob.Services.AddSingleton(new TenantAccessClient(ob.Configuration, new HttpClient(authority,false)));
            ob.Services.AddSingleton(new TenantBoundaryOptions { Service="financial-profile", CapabilityPrefix="financial-profile.", CustomerTenants=new Dictionary<string,string> { [Customer]="T001" }, ProbeUrl="http://127.0.0.1:5199/" });
            ob.Services.AddSingleton(new TaxRecordsApplication(financial,records,TimeProvider.System)); var owner=ob.Build(); owner.MapTaxRecords(); await owner.StartAsync();
            values["Monergy:Product:FinancialProfileUrl"]=owner.Urls.Single()+"/";
            var gb=WebApplication.CreateSlimBuilder(); gb.WebHost.UseUrls("http://127.0.0.1:0"); gb.Configuration.AddInMemoryCollection(values);
            gb.Services.AddSingleton(new CustomerOwnerClient(gb.Configuration,new HttpClient(authority,false)));
            gb.Services.AddSingleton(new TenantAccessClient(gb.Configuration,new HttpClient(authority,false))); gb.AddProductTaxRecords();
            if(tamper is not null) gb.Services.AddSingleton(new ProductTaxRecordClient(gb.Configuration,new HttpClient(new TamperHandler(tamper))));
            var gateway=gb.Build(); gateway.MapProductTaxRecords(); await gateway.StartAsync(); return new(owner,gateway,authority,records,source);
        }
        public HttpClient Client(string? tenant="T001") => ClientFor(gateway,tenant);
        public HttpClient OwnerClient(string tenant="T001") => ClientFor(owner,tenant);
        private static HttpClient ClientFor(WebApplication app,string? tenant)
        {
            var client=new HttpClient { BaseAddress=new Uri(app.Urls.Single()) };
            if(tenant is not null) { client.DefaultRequestHeaders.Add("X-Monergy-Tenant",tenant);client.DefaultRequestHeaders.Add("X-Monergy-Session","session-001"); }
            return client;
        }
        public async ValueTask DisposeAsync() { await gateway.DisposeAsync();await owner.DisposeAsync();authority.Dispose(); }
    }
}
