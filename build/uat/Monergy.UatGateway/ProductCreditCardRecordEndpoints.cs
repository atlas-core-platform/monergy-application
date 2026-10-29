using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Json;
using System.Text.Json;
using Monergy.Contracts;
using Monergy.Platform;

namespace Monergy.UatGateway;

public sealed record ProductCreditCardRecordList(
    IReadOnlyList<ReviewedCreditCardRecord> Items, bool HasMore, string DataDurability);

public static class ProductCreditCardRecordEndpoints
{
    private static readonly JsonSerializerOptions Json = new(ContractJson.Options)
    { RespectRequiredConstructorParameters=true,RespectNullableAnnotations=true,
        UnmappedMemberHandling=System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };

    public static void AddProductCreditCardRecords(this WebApplicationBuilder builder)
    {
        if(builder.Configuration.GetValue<bool>("Monergy:Product:Enabled"))
            builder.Services.AddSingleton(_=>new ProductCreditCardRecordClient(builder.Configuration));
    }

    public static void MapProductCreditCardRecords(this WebApplication app)
    {
        if(!app.Configuration.GetValue<bool>("Monergy:Product:Enabled"))return;
        var group=app.MapGroup("/product-api/v1/credit-card-records");
        group.AddEndpointFilter(async(context,next)=>
        {
            var http=context.HttpContext;http.Response.Headers.CacheControl="no-store";
            if(http.Request.Headers.Origin.Count>0&&http.Request.Headers.Origin.ToString()!=$"{http.Request.Scheme}://{http.Request.Host}")return Results.StatusCode(403);
            try{return await next(context).ConfigureAwait(false);}
            catch(TenantBoundaryException error){return Results.Json(new{error=error.Code},statusCode:error.Status);}
            catch(Exception error) when(error is HttpRequestException or JsonException or NotSupportedException)
            {return Results.Json(new{error="CREDIT_CARD_RECORD_UNAVAILABLE"},statusCode:503);}
            catch(IOException){return Results.Json(new{error="CREDIT_CARD_RECORD_TOO_LARGE"},statusCode:413);}
            catch(OperationCanceledException) when(!http.RequestAborted.IsCancellationRequested)
            {return Results.Json(new{error="CREDIT_CARD_RECORD_UNAVAILABLE"},statusCode:503);}
        });
        group.MapGet("",async(HttpContext http,CustomerOwnerClient owners,TenantAccessClient access,
            ProductCreditCardRecordClient client)=>
        {
            var(session,customer)=await ResolveAsync(http,owners,access,false).ConfigureAwait(false);
            var after=ReviewedRecordPaging.ReadAfter(http.Request);
            var list=await client.ListAsync(session,customer.CustomerId,http.RequestAborted,after).ConfigureAwait(false);
            if(list.Items is null||list.Items.Count>200||list.HasMore&&list.Items.Count!=200||
                list.Items.Select(item=>item.CreditCardRecordId).Distinct(StringComparer.Ordinal).Count()!=list.Items.Count||
                list.Items.Any(item=>!Bound(item,customer.CustomerId)||item.CreditCardRecordId==after))InvalidResponse();
            await ConfirmAsync(http,owners,access,session,customer,false).ConfigureAwait(false);
            return new ProductCreditCardRecordList(list.Items,list.HasMore,"Temporary data");
        });
        group.MapPost("",async(HttpContext http,CustomerOwnerClient owners,TenantAccessClient access,
            ProductCreditCardRecordClient client)=>
        {
            var(session,customer)=await ResolveAsync(http,owners,access,true).ConfigureAwait(false);
            var input=await ReadAsync<CreateCreditCardRecord>(http).ConfigureAwait(false);
            var receipt=await client.CreateAsync(session,customer.CustomerId,input,http.RequestAborted).ConfigureAwait(false);
            if(!Bound(receipt,customer.CustomerId)||receipt.OperationId!=input.OperationId||receipt.RecordedBy!=session.ActorId||
                receipt.Card!=input.Card with{IssuerName=input.Card.IssuerName.Trim(),CardType=input.Card.CardType.Trim()})InvalidResponse();
            var saved=await client.ReadAsync(session,customer.CustomerId,receipt.CreditCardRecordId,http.RequestAborted,1).ConfigureAwait(false);
            if(saved!=receipt)InvalidResponse();
            await ConfirmAsync(http,owners,access,session,customer,true).ConfigureAwait(false);return saved;
        });
        group.MapGet("/{id}/history",async(string id,int? beforeRevision,HttpContext http,CustomerOwnerClient owners,
            TenantAccessClient access,ProductCreditCardRecordClient client)=>
        {
            var(session,customer)=await ResolveAsync(http,owners,access,false).ConfigureAwait(false);
            if(!TenantAccessClient.Identifier(id,96)||beforeRevision is <1)throw new TenantBoundaryException("CREDIT_CARD_RECORD_INVALID",400);
            var history=await client.HistoryAsync(session,customer.CustomerId,id,beforeRevision,http.RequestAborted).ConfigureAwait(false);
            if(history.Items.Count>50||history.Items.Any(item=>!Bound(item,customer.CustomerId)||item.CreditCardRecordId!=id||beforeRevision is not null&&item.Revision>=beforeRevision)||
                history.Items.Zip(history.Items.Skip(1)).Any(pair=>pair.First.Revision!=pair.Second.Revision+1)||
                history.HasMore&&(history.Items.Count!=50||history.NextBeforeRevision!=history.Items[^1].Revision)||!history.HasMore&&history.NextBeforeRevision is not null)InvalidResponse();
            await ConfirmAsync(http,owners,access,session,customer,false).ConfigureAwait(false);return history;
        });
        group.MapPost("/{id}/corrections",async(string id,HttpContext http,CustomerOwnerClient owners,
            TenantAccessClient access,ProductCreditCardRecordClient client)=>
        {
            var(session,customer)=await ResolveAsync(http,owners,access,true).ConfigureAwait(false);
            if(!TenantAccessClient.Identifier(id,96))throw new TenantBoundaryException("CREDIT_CARD_RECORD_INVALID",400);
            var input=await ReadAsync<ReviseCreditCardRecord>(http).ConfigureAwait(false);
            var original=await client.ReadAsync(session,customer.CustomerId,id,http.RequestAborted,1).ConfigureAwait(false);
            var receipt=await client.ReviseAsync(session,customer.CustomerId,id,input,http.RequestAborted).ConfigureAwait(false);
            if(!Bound(original,customer.CustomerId)||!Bound(receipt,customer.CustomerId)||original.Revision!=1||
                receipt.CreditCardRecordId!=id||receipt.OperationId!=input.OperationId||receipt.RecordedBy!=session.ActorId||
                receipt.Revision!=input.ExpectedRevision+1||receipt.Card!=input.Card with{IssuerName=input.Card.IssuerName.Trim(),CardType=input.Card.CardType.Trim()}||
                receipt.Balance!=original.Balance||receipt.Currency!=original.Currency||receipt.BalanceAsOfDate!=original.BalanceAsOfDate||
                receipt.SourceProvenanceId!=original.SourceProvenanceId||receipt.DocumentVersionId!=original.DocumentVersionId||receipt.EvidenceId!=original.EvidenceId||
                receipt.Card.SourceFinancialFactId!=original.Card.SourceFinancialFactId||receipt.Card.SourceRevision!=original.Card.SourceRevision)InvalidResponse();
            var saved=await client.ReadAsync(session,customer.CustomerId,id,http.RequestAborted,receipt.Revision).ConfigureAwait(false);
            if(saved!=receipt)InvalidResponse();
            await ConfirmAsync(http,owners,access,session,customer,true).ConfigureAwait(false);return saved;
        });
    }

    private static async Task<T> ReadAsync<T>(HttpContext http) where T:class
    {
        try{http.Request.EnableBuffering(32768,32768);using var json=await JsonDocument.ParseAsync(http.Request.Body,cancellationToken:http.RequestAborted).ConfigureAwait(false);
            RejectDuplicates(json.RootElement);return json.RootElement.Deserialize<T>(Json)??throw new JsonException();}
        catch(JsonException){throw new TenantBoundaryException("CREDIT_CARD_RECORD_INVALID",400);}
    }
    private static void RejectDuplicates(JsonElement element)
    {
        if(element.ValueKind==JsonValueKind.Object){var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);foreach(var property in element.EnumerateObject())
            {if(!names.Add(property.Name))throw new JsonException();RejectDuplicates(property.Value);}}
        else if(element.ValueKind==JsonValueKind.Array)foreach(var item in element.EnumerateArray())RejectDuplicates(item);
    }
    internal static bool Bound(ReviewedCreditCardRecord? value,string customer)=>value is{Card:not null}&&value.CustomerId==customer&&
        value.Revision>0&&TenantAccessClient.Identifier(value.CreditCardRecordId,96)&&Guid.TryParseExact(value.OperationId,"N",out _)&&
        Text(value.Card.IssuerName)&&Text(value.Card.CardType)&&value.Card.CardNumberLast4.Length==4&&value.Card.CardNumberLast4.All(char.IsAsciiDigit)&&
        value.Balance is>=0 and<=999999999999999m&&decimal.Round(value.Balance,2)==value.Balance&&
        value.Currency.Length==3&&value.Currency.All(char.IsAsciiLetterUpper)&&value.BalanceAsOfDate.Year>=1900&&
        TenantAccessClient.Identifier(value.Card.SourceFinancialFactId,160)&&value.Card.SourceRevision>0&&
        TenantAccessClient.Identifier(value.SourceProvenanceId,160)&&TenantAccessClient.Identifier(value.DocumentVersionId,160)&&
        TenantAccessClient.Identifier(value.EvidenceId,160)&&TenantAccessClient.Identifier(value.RecordedBy,160)&&value.RecordedAt!=default;
    private static bool Text(string? value)=>!string.IsNullOrWhiteSpace(value)&&value.Trim().Length<=120&&!value.Any(char.IsControl);
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void InvalidResponse()=>throw new TenantBoundaryException("CREDIT_CARD_RECORD_RESPONSE_INVALID",503);
    private static async Task<(CustomerOwnerSession Session,CustomerOwnerWorkspace Customer)> ResolveAsync(HttpContext http,
        CustomerOwnerClient owners,TenantAccessClient access,bool write)
    {
        var session=await owners.SessionAsync(http).ConfigureAwait(false);
        var customer=await owners.OwnedCustomerAsync(session.TenantId,session.ActorId,http.RequestAborted).ConfigureAwait(false)
            ??throw new TenantBoundaryException("PRODUCT_CUSTOMER_REQUIRED",403);
        var read=await access.EvaluateCustomerAsync(http,"financial-profile.profile.read",customer.CustomerId).ConfigureAwait(false);
        var evidence=await access.EvaluateCustomerAsync(http,"evidence.document.read",customer.CustomerId).ConfigureAwait(false);
        if(read.Outcome!="Allow"||read.AccessBasis!="Self"||read.ActorId!=session.ActorId||read.SubjectVersion!=session.SubjectVersion||
            evidence.Outcome!="Allow"||evidence.AccessBasis!="Self"||evidence.ActorId!=read.ActorId||evidence.SubjectVersion!=read.SubjectVersion||evidence.CustomerVersion!=read.CustomerVersion)
            throw new TenantBoundaryException("ACCESS_DENIED",403);
        if(write){var update=await access.EvaluateCustomerAsync(http,"financial-profile.profile.update",customer.CustomerId).ConfigureAwait(false);
            if(update.Outcome!="Allow"||update.AccessBasis!="Self"||update.ActorId!=session.ActorId||update.SubjectVersion!=read.SubjectVersion||update.CustomerVersion!=read.CustomerVersion)
                throw new TenantBoundaryException("ACCESS_DENIED",403);}
        return(session,customer);
    }
    private static async Task ConfirmAsync(HttpContext http,CustomerOwnerClient owners,TenantAccessClient access,
        CustomerOwnerSession original,CustomerOwnerWorkspace originalCustomer,bool write)
    {
        var(current,customer)=await ResolveAsync(http,owners,access,write).ConfigureAwait(false);
        if(current.TenantId!=original.TenantId||current.ActorId!=original.ActorId||current.SubjectVersion!=original.SubjectVersion||
            customer.CustomerId!=originalCustomer.CustomerId||customer.Version!=originalCustomer.Version)
            throw new TenantBoundaryException("CREDIT_CARD_RECORD_CONTEXT_CHANGED",409);
    }
}

public sealed class ProductCreditCardRecordClient:IDisposable
{
    private static readonly JsonSerializerOptions Json=new(ContractJson.Options)
    {RespectRequiredConstructorParameters=true,RespectNullableAnnotations=true,UnmappedMemberHandling=System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow};
    private readonly HttpClient client;private readonly Uri endpoint;
    public ProductCreditCardRecordClient(IConfiguration configuration):this(configuration,CreateClient()){}
    public ProductCreditCardRecordClient(IConfiguration configuration,HttpClient httpClient)
    {client=httpClient;endpoint=TenantAccessIntegration.LoopbackRoot(TenantAccessIntegration.Required(configuration,"Monergy:Product:FinancialProfileUrl"));}
    private static HttpClient CreateClient()=>new(new SocketsHttpHandler{AllowAutoRedirect=false,UseProxy=false})
    {Timeout=TimeSpan.FromSeconds(10),MaxResponseContentBufferSize=1048576};
    public Task<CreditCardRecordList> ListAsync(CustomerOwnerSession session,string customer,CancellationToken ct,string? after=null)=>
        SendAsync<CreditCardRecordList>(session,customer,after is null?"":"?afterId="+Uri.EscapeDataString(after),null,ct);
    public Task<ReviewedCreditCardRecord> ReadAsync(CustomerOwnerSession session,string customer,string id,CancellationToken ct,int? revision=null)=>
        SendAsync<ReviewedCreditCardRecord>(session,customer,"/"+Uri.EscapeDataString(id)+(revision is null?"":"?revision="+revision.Value),null,ct);
    public Task<ReviewedCreditCardRecord> CreateAsync(CustomerOwnerSession session,string customer,CreateCreditCardRecord input,CancellationToken ct)=>
        SendAsync<ReviewedCreditCardRecord>(session,customer,"",input,ct);
    public Task<ReviewedCreditCardRecord> ReviseAsync(CustomerOwnerSession session,string customer,string id,ReviseCreditCardRecord input,CancellationToken ct)=>
        SendAsync<ReviewedCreditCardRecord>(session,customer,"/"+Uri.EscapeDataString(id)+"/corrections",input,ct);
    public Task<CreditCardRecordHistory> HistoryAsync(CustomerOwnerSession session,string customer,string id,int? before,CancellationToken ct)=>
        SendAsync<CreditCardRecordHistory>(session,customer,"/"+Uri.EscapeDataString(id)+"/history"+(before is null?"":"?beforeRevision="+before.Value),null,ct);
    private async Task<T> SendAsync<T>(CustomerOwnerSession session,string customer,string suffix,object? input,CancellationToken ct)
    {
        using var request=new HttpRequestMessage(input is null?HttpMethod.Get:HttpMethod.Post,
            new Uri(endpoint,"local/v1/customers/"+Uri.EscapeDataString(customer)+"/credit-card-records"+suffix));
        request.Headers.Add("X-Monergy-Tenant",session.TenantId);request.Headers.Add("X-Monergy-Session",session.AuthenticationContextId);
        if(input is not null)request.Content=JsonContent.Create(input,options:ContractJson.Options);
        using var response=await client.SendAsync(request,ct).ConfigureAwait(false);
        if(!response.IsSuccessStatusCode)
        {
            var status=(int)response.StatusCode;var code=status switch{401=>"TENANT_SESSION_INVALID",403=>"ACCESS_DENIED",404=>"CREDIT_CARD_RECORD_NOT_FOUND",
                409=>"CREDIT_CARD_OPERATION_CONFLICT",400=>"CREDIT_CARD_RECORD_INVALID",413=>"CREDIT_CARD_RECORD_TOO_LARGE",_=>"CREDIT_CARD_RECORD_UNAVAILABLE"};
            if(status==409){var error=await response.Content.ReadFromJsonAsync<JsonElement>(ct).ConfigureAwait(false);
                if(error.TryGetProperty("error",out var value)&&value.GetString()=="CREDIT_CARD_REVISION_CONFLICT")code="CREDIT_CARD_REVISION_CONFLICT";}
            throw new TenantBoundaryException(code,status is 400 or 401 or 403 or 404 or 409 or 413?status:503);
        }
        return await response.Content.ReadFromJsonAsync<T>(Json,ct).ConfigureAwait(false)??throw new TenantBoundaryException("CREDIT_CARD_RECORD_RESPONSE_INVALID",503);
    }
    public void Dispose()=>client.Dispose();
}
