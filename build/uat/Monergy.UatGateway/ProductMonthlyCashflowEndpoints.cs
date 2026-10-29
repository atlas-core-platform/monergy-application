using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Monergy.Contracts;
using Monergy.Platform;
namespace Monergy.UatGateway;

public static class ProductMonthlyCashflowEndpoints
{
    public static void AddProductMonthlyCashflow(this WebApplicationBuilder builder)
    {if(builder.Configuration.GetValue<bool>("Monergy:Product:Enabled"))builder.Services.AddSingleton<ProductMonthlyCashflowClient>();}
    public static void MapProductMonthlyCashflow(this WebApplication app)
    {
        if(!app.Configuration.GetValue<bool>("Monergy:Product:Enabled"))return;
        var group=app.MapGroup("/product-api/v1/monthly-cashflow");group.AddEndpointFilter(CustomerReviewHttp.Filter);
        group.MapPost("/comparison",async(HttpContext http,CustomerOwnerClient owners,TenantAccessClient access,ProductMonthlyCashflowClient client)=>
        {
            CustomerReviewHttp.NoQuery(http.Request);var(session,customer,before)=await Resolve(http,owners,access,calculate:true).ConfigureAwait(false);
            var input=await CustomerReviewHttp.ReadAsync<CompareMonthlyCashflow>(http).ConfigureAwait(false);Identifier(input.EarlierCalculationId);Identifier(input.LaterCalculationId);
            var value=await client.Compare(session,customer.CustomerId,input,http.RequestAborted).ConfigureAwait(false);
            if(!MonthlyCashflowData.Total(value.IncomeChange,true)||!MonthlyCashflowData.Total(value.ExpenseChange,true)||!MonthlyCashflowData.Total(value.BalanceChange,true)||!Bound(value.Earlier,customer.CustomerId)||!Bound(value.Later,customer.CustomerId)||value.Earlier.CalculationId!=input.EarlierCalculationId||value.Later.CalculationId!=input.LaterCalculationId||
                value.Earlier.State!="CALCULATED"||value.Later.State!="CALCULATED"||value.Earlier.Position.Position.AsOfDate>=value.Later.Position.Position.AsOfDate||value.Earlier.Position.Position.Currency!=value.Later.Position.Position.Currency||value.Earlier.Position.Position.Household!=value.Later.Position.Position.Household)throw Invalid();
            if(!MonthlyCashflowData.Same(value.Earlier,await client.Calculation(session,customer.CustomerId,input.EarlierCalculationId,http.RequestAborted).ConfigureAwait(false))||
                !MonthlyCashflowData.Same(value.Later,await client.Calculation(session,customer.CustomerId,input.LaterCalculationId,http.RequestAborted).ConfigureAwait(false)))throw Invalid();
            await Confirm(http,owners,access,session,customer,before,calculate:true,documents:value.Earlier.Position.Lines.Concat(value.Later.Position.Lines).Any(x=>x.Source is not null)).ConfigureAwait(false);return value;
        });
        group.MapPost("/positions",async(HttpContext http,CustomerOwnerClient owners,TenantAccessClient access,ProductMonthlyCashflowClient client)=>
        {
            CustomerReviewHttp.NoQuery(http.Request);var (session,customer,before)=await Resolve(http,owners,access,write:true).ConfigureAwait(false);
            var input=await CustomerReviewHttp.ReadAsync<CreateMonthlyCashflowPosition>(http).ConfigureAwait(false);
            if(!MonthlyCashflowData.Operation(input.OperationId)||!MonthlyCashflowData.Draft(input.Position,DateTimeOffset.UtcNow))throw new TenantBoundaryException("MONTHLY_CASHFLOW_INVALID",400);
            var receipt=await client.SavePosition(session,customer.CustomerId,input,http.RequestAborted).ConfigureAwait(false);
            if(!MonthlyCashflowData.Bound(receipt,customer.CustomerId,DateTimeOffset.UtcNow)||receipt.OperationId!=input.OperationId||receipt.RecordedBy!=session.ActorId||!MonthlyCashflowData.Same(receipt.Position,input.Position))throw Invalid();
            var saved=await client.Position(session,customer.CustomerId,receipt.PositionId,http.RequestAborted).ConfigureAwait(false);
            if(!MonthlyCashflowData.Same(saved,receipt))throw Invalid();
            await Confirm(http,owners,access,session,customer,before,write:true,documents:receipt.Lines.Any(x=>x.Source is not null)).ConfigureAwait(false);return saved;
        });
        group.MapGet("/positions",async(HttpContext http,CustomerOwnerClient owners,TenantAccessClient access,ProductMonthlyCashflowClient client)=>
        {
            var (session,customer,before)=await Resolve(http,owners,access).ConfigureAwait(false);var cursor=CustomerReviewHttp.Cursor(http.Request);
            var page=await client.Positions(session,customer.CustomerId,cursor,http.RequestAborted).ConfigureAwait(false);
            if(!Page(page.Items,page.HasMore,page.NextBeforeId,x=>MonthlyCashflowData.Bound(x,customer.CustomerId,DateTimeOffset.UtcNow),x=>x.PositionId)||page.Items.Any(x=>x.PositionId==cursor))throw Invalid();
            await Confirm(http,owners,access,session,customer,before,documents:page.Items.Any(x=>x.Lines.Any(y=>y.Source is not null))).ConfigureAwait(false);return page;
        });
        group.MapGet("/positions/{positionId}",async(string positionId,HttpContext http,CustomerOwnerClient owners,TenantAccessClient access,ProductMonthlyCashflowClient client)=>
        {
            CustomerReviewHttp.NoQuery(http.Request);Identifier(positionId);var (session,customer,before)=await Resolve(http,owners,access).ConfigureAwait(false);
            var value=await client.Position(session,customer.CustomerId,positionId,http.RequestAborted).ConfigureAwait(false);
            if(!MonthlyCashflowData.Bound(value,customer.CustomerId,DateTimeOffset.UtcNow)||value.PositionId!=positionId)throw Invalid();
            await Confirm(http,owners,access,session,customer,before,documents:value.Lines.Any(x=>x.Source is not null)).ConfigureAwait(false);return value;
        });
        group.MapPost("/calculations",async(HttpContext http,CustomerOwnerClient owners,TenantAccessClient access,ProductMonthlyCashflowClient client)=>
        {
            CustomerReviewHttp.NoQuery(http.Request);var (session,customer,before)=await Resolve(http,owners,access,calculate:true).ConfigureAwait(false);
            var input=await CustomerReviewHttp.ReadAsync<CalculateMonthlyCashflow>(http).ConfigureAwait(false);
            if(!MonthlyCashflowData.Operation(input.OperationId))throw new TenantBoundaryException("MONTHLY_CASHFLOW_INVALID",400);Identifier(input.PositionId);
            var position=await client.Position(session,customer.CustomerId,input.PositionId,http.RequestAborted).ConfigureAwait(false);
            if(!MonthlyCashflowData.Bound(position,customer.CustomerId,DateTimeOffset.UtcNow)||position.PositionId!=input.PositionId)throw Invalid();
            var receipt=await client.Calculate(session,customer.CustomerId,input,http.RequestAborted).ConfigureAwait(false);
            if(!Bound(receipt,customer.CustomerId)||receipt.OperationId!=input.OperationId||receipt.RecordedBy!=session.ActorId||!MonthlyCashflowData.Same(receipt.Position,position))throw Invalid();
            var saved=await client.Calculation(session,customer.CustomerId,receipt.CalculationId,http.RequestAborted).ConfigureAwait(false);
            if(!MonthlyCashflowData.Same(receipt,saved))throw Invalid();
            await Confirm(http,owners,access,session,customer,before,calculate:true,documents:position.Lines.Any(x=>x.Source is not null)).ConfigureAwait(false);return saved;
        });
        group.MapGet("/calculations/{calculationId}",async(string calculationId,HttpContext http,CustomerOwnerClient owners,TenantAccessClient access,ProductMonthlyCashflowClient client)=>
        {
            CustomerReviewHttp.NoQuery(http.Request);Identifier(calculationId);var (session,customer,before)=await Resolve(http,owners,access,calculate:true).ConfigureAwait(false);
            var value=await client.Calculation(session,customer.CustomerId,calculationId,http.RequestAborted).ConfigureAwait(false);
            if(!Bound(value,customer.CustomerId)||value.CalculationId!=calculationId)throw Invalid();
            var position=await client.Position(session,customer.CustomerId,value.Position.PositionId,http.RequestAborted).ConfigureAwait(false);
            if(!MonthlyCashflowData.Same(position,value.Position))throw Invalid();
            await Confirm(http,owners,access,session,customer,before,calculate:true,documents:position.Lines.Any(x=>x.Source is not null)).ConfigureAwait(false);return value;
        });
        group.MapGet("/calculations",async(HttpContext http,CustomerOwnerClient owners,TenantAccessClient access,ProductMonthlyCashflowClient client)=>
        {
            var (session,customer,before)=await Resolve(http,owners,access,calculate:true).ConfigureAwait(false);var cursor=CustomerReviewHttp.Cursor(http.Request);
            var page=await client.Calculations(session,customer.CustomerId,cursor,http.RequestAborted).ConfigureAwait(false);
            if(!Page(page.Items,page.HasMore,page.NextBeforeId,x=>Bound(x,customer.CustomerId),x=>x.CalculationId)||page.Items.Any(x=>x.CalculationId==cursor))throw Invalid();
            await Confirm(http,owners,access,session,customer,before,calculate:true,documents:page.Items.Any(x=>x.Position.Lines.Any(y=>y.Source is not null))).ConfigureAwait(false);return page;
        });
        group.MapPost("/debt-service-ratios",async(HttpContext http,CustomerOwnerClient owners,TenantAccessClient access,ProductMonthlyCashflowClient client)=>
        {
            CustomerReviewHttp.NoQuery(http.Request);var(session,customer,before)=await Resolve(http,owners,access,calculate:true).ConfigureAwait(false);
            var input=await CustomerReviewHttp.ReadAsync<CalculateDebtServiceRatio>(http).ConfigureAwait(false);if(!DebtServiceRatioData.Request(input))throw new TenantBoundaryException("DEBT_SERVICE_RATIO_INVALID",400);
            var source=await client.Calculation(session,customer.CustomerId,input.MonthlyCashflowCalculationId,http.RequestAborted).ConfigureAwait(false);if(!Bound(source,customer.CustomerId))throw Invalid();
            var receipt=await client.CalculateDebtServiceRatio(session,customer.CustomerId,input,http.RequestAborted).ConfigureAwait(false);
            if(!DebtServiceRatioData.Calculation(receipt,customer.CustomerId)||receipt.OperationId!=input.OperationId||receipt.RecordedBy!=session.ActorId||
                receipt.SourceCalculation.CalculationId!=input.MonthlyCashflowCalculationId||!receipt.DebtPaymentLineIds.SequenceEqual(input.DebtPaymentLineIds)||receipt.DebtPaymentsComplete!=input.DebtPaymentsComplete||!MonthlyCashflowData.Same(receipt.SourceCalculation,source))throw Invalid();
            var saved=await client.DebtServiceRatio(session,customer.CustomerId,receipt.CalculationId,http.RequestAborted).ConfigureAwait(false);if(!MonthlyCashflowData.Same(saved,receipt))throw Invalid();
            await Confirm(http,owners,access,session,customer,before,calculate:true,documents:source.Position.Lines.Any(x=>x.Source is not null)).ConfigureAwait(false);return saved;
        });
        group.MapGet("/debt-service-ratios/{calculationId}",async(string calculationId,HttpContext http,CustomerOwnerClient owners,TenantAccessClient access,ProductMonthlyCashflowClient client)=>
        {
            CustomerReviewHttp.NoQuery(http.Request);Identifier(calculationId);var(session,customer,before)=await Resolve(http,owners,access,calculate:true).ConfigureAwait(false);
            var value=await client.DebtServiceRatio(session,customer.CustomerId,calculationId,http.RequestAborted).ConfigureAwait(false);if(!DebtServiceRatioData.Calculation(value,customer.CustomerId)||value.CalculationId!=calculationId)throw Invalid();
            var source=await client.Calculation(session,customer.CustomerId,value.SourceCalculation.CalculationId,http.RequestAborted).ConfigureAwait(false);if(!MonthlyCashflowData.Same(source,value.SourceCalculation))throw Invalid();
            await Confirm(http,owners,access,session,customer,before,calculate:true,documents:source.Position.Lines.Any(x=>x.Source is not null)).ConfigureAwait(false);return value;
        });
        group.MapGet("/debt-service-ratios",async(HttpContext http,CustomerOwnerClient owners,TenantAccessClient access,ProductMonthlyCashflowClient client)=>
        {
            var(session,customer,before)=await Resolve(http,owners,access,calculate:true).ConfigureAwait(false);var cursor=CustomerReviewHttp.Cursor(http.Request);
            var page=await client.DebtServiceRatios(session,customer.CustomerId,cursor,http.RequestAborted).ConfigureAwait(false);
            if(!Page(page.Items,page.HasMore,page.NextBeforeId,x=>DebtServiceRatioData.Calculation(x,customer.CustomerId),x=>x.CalculationId)||page.Items.Any(x=>x.CalculationId==cursor))throw Invalid();
            await Confirm(http,owners,access,session,customer,before,calculate:true,documents:page.Items.Any(x=>x.SourceCalculation.Position.Lines.Any(y=>y.Source is not null))).ConfigureAwait(false);return page;
        });
        group.MapPost("/debt-to-income-ratios",async(HttpContext http,CustomerOwnerClient owners,TenantAccessClient access,ProductMonthlyCashflowClient client)=>
        {
            CustomerReviewHttp.NoQuery(http.Request);var(session,customer,before)=await Resolve(http,owners,access,calculate:true).ConfigureAwait(false);
            var input=await CustomerReviewHttp.ReadAsync<CalculateDebtToIncome>(http).ConfigureAwait(false);if(!DebtToIncomeData.Request(input))throw new TenantBoundaryException("DEBT_TO_INCOME_INVALID",400);
            var source=await client.DebtServiceRatio(session,customer.CustomerId,input.DebtServiceRatioCalculationId,http.RequestAborted).ConfigureAwait(false);if(!DebtServiceRatioData.Calculation(source,customer.CustomerId))throw Invalid();
            var receipt=await client.CalculateDebtToIncome(session,customer.CustomerId,input,http.RequestAborted).ConfigureAwait(false);
            if(!DebtToIncomeData.Calculation(receipt,customer.CustomerId)||receipt.OperationId!=input.OperationId||receipt.RecordedBy!=session.ActorId||receipt.GrossIncomeConfirmed!=input.GrossIncomeConfirmed||receipt.SourceDebtServiceRatio.CalculationId!=input.DebtServiceRatioCalculationId||!MonthlyCashflowData.Same(receipt.SourceDebtServiceRatio,source))throw Invalid();
            var saved=await client.DebtToIncome(session,customer.CustomerId,receipt.CalculationId,http.RequestAborted).ConfigureAwait(false);if(!MonthlyCashflowData.Same(saved,receipt))throw Invalid();
            await Confirm(http,owners,access,session,customer,before,calculate:true,documents:source.SourceCalculation.Position.Lines.Any(x=>x.Source is not null)).ConfigureAwait(false);return saved;
        });
        group.MapGet("/debt-to-income-ratios/{calculationId}",async(string calculationId,HttpContext http,CustomerOwnerClient owners,TenantAccessClient access,ProductMonthlyCashflowClient client)=>
        {
            CustomerReviewHttp.NoQuery(http.Request);Identifier(calculationId);var(session,customer,before)=await Resolve(http,owners,access,calculate:true).ConfigureAwait(false);
            var value=await client.DebtToIncome(session,customer.CustomerId,calculationId,http.RequestAborted).ConfigureAwait(false);if(!DebtToIncomeData.Calculation(value,customer.CustomerId)||value.CalculationId!=calculationId)throw Invalid();
            var source=await client.DebtServiceRatio(session,customer.CustomerId,value.SourceDebtServiceRatio.CalculationId,http.RequestAborted).ConfigureAwait(false);if(!MonthlyCashflowData.Same(source,value.SourceDebtServiceRatio))throw Invalid();
            await Confirm(http,owners,access,session,customer,before,calculate:true,documents:source.SourceCalculation.Position.Lines.Any(x=>x.Source is not null)).ConfigureAwait(false);return value;
        });
        group.MapGet("/debt-to-income-ratios",async(HttpContext http,CustomerOwnerClient owners,TenantAccessClient access,ProductMonthlyCashflowClient client)=>
        {
            var(session,customer,before)=await Resolve(http,owners,access,calculate:true).ConfigureAwait(false);var cursor=CustomerReviewHttp.Cursor(http.Request);
            var page=await client.DebtToIncomes(session,customer.CustomerId,cursor,http.RequestAborted).ConfigureAwait(false);
            if(!Page(page.Items,page.HasMore,page.NextBeforeId,x=>DebtToIncomeData.Calculation(x,customer.CustomerId),x=>x.CalculationId)||page.Items.Any(x=>x.CalculationId==cursor))throw Invalid();
            await Confirm(http,owners,access,session,customer,before,calculate:true,documents:page.Items.Any(x=>x.SourceDebtServiceRatio.SourceCalculation.Position.Lines.Any(y=>y.Source is not null))).ConfigureAwait(false);return page;
        });
        group.MapPost("/net-cashflow-rates",async(HttpContext http,CustomerOwnerClient owners,TenantAccessClient access,ProductMonthlyCashflowClient client)=>
        {
            CustomerReviewHttp.NoQuery(http.Request);var(session,customer,before)=await Resolve(http,owners,access,calculate:true).ConfigureAwait(false);
            var input=await CustomerReviewHttp.ReadAsync<CalculateMonthlyNetCashflowRate>(http).ConfigureAwait(false);if(!MonthlyNetCashflowRateData.Request(input))throw new TenantBoundaryException("MONTHLY_NET_CASHFLOW_RATE_INVALID",400);
            var source=await client.Calculation(session,customer.CustomerId,input.MonthlyCashflowCalculationId,http.RequestAborted).ConfigureAwait(false);if(!Bound(source,customer.CustomerId))throw Invalid();
            var receipt=await client.CalculateMonthlyNetCashflowRate(session,customer.CustomerId,input,http.RequestAborted).ConfigureAwait(false);
            if(!MonthlyNetCashflowRateData.Calculation(receipt,customer.CustomerId)||receipt.OperationId!=input.OperationId||receipt.RecordedBy!=session.ActorId||receipt.SourceMonthlyCashflow.CalculationId!=input.MonthlyCashflowCalculationId||!MonthlyCashflowData.Same(receipt.SourceMonthlyCashflow,source))throw Invalid();
            var saved=await client.MonthlyNetCashflowRate(session,customer.CustomerId,receipt.CalculationId,http.RequestAborted).ConfigureAwait(false);if(!MonthlyCashflowData.Same(saved,receipt))throw Invalid();
            await Confirm(http,owners,access,session,customer,before,calculate:true,documents:source.Position.Lines.Any(x=>x.Source is not null)).ConfigureAwait(false);return saved;
        });
        group.MapGet("/net-cashflow-rates/{calculationId}",async(string calculationId,HttpContext http,CustomerOwnerClient owners,TenantAccessClient access,ProductMonthlyCashflowClient client)=>
        {
            CustomerReviewHttp.NoQuery(http.Request);Identifier(calculationId);var(session,customer,before)=await Resolve(http,owners,access,calculate:true).ConfigureAwait(false);
            var value=await client.MonthlyNetCashflowRate(session,customer.CustomerId,calculationId,http.RequestAborted).ConfigureAwait(false);if(!MonthlyNetCashflowRateData.Calculation(value,customer.CustomerId)||value.CalculationId!=calculationId)throw Invalid();
            var source=await client.Calculation(session,customer.CustomerId,value.SourceMonthlyCashflow.CalculationId,http.RequestAborted).ConfigureAwait(false);if(!MonthlyCashflowData.Same(source,value.SourceMonthlyCashflow))throw Invalid();
            await Confirm(http,owners,access,session,customer,before,calculate:true,documents:source.Position.Lines.Any(x=>x.Source is not null)).ConfigureAwait(false);return value;
        });
        group.MapGet("/net-cashflow-rates",async(HttpContext http,CustomerOwnerClient owners,TenantAccessClient access,ProductMonthlyCashflowClient client)=>
        {
            var(session,customer,before)=await Resolve(http,owners,access,calculate:true).ConfigureAwait(false);var cursor=CustomerReviewHttp.Cursor(http.Request);
            var page=await client.MonthlyNetCashflowRates(session,customer.CustomerId,cursor,http.RequestAborted).ConfigureAwait(false);
            if(!Page(page.Items,page.HasMore,page.NextBeforeId,x=>MonthlyNetCashflowRateData.Calculation(x,customer.CustomerId),x=>x.CalculationId)||page.Items.Any(x=>x.CalculationId==cursor))throw Invalid();
            await Confirm(http,owners,access,session,customer,before,calculate:true,documents:page.Items.Any(x=>x.SourceMonthlyCashflow.Position.Lines.Any(y=>y.Source is not null))).ConfigureAwait(false);return page;
        });
        group.MapPost("/emi-shares",async(HttpContext http,CustomerOwnerClient owners,TenantAccessClient access,ProductMonthlyCashflowClient client,ProductLoanRecordClient loans)=>
        {
            CustomerReviewHttp.NoQuery(http.Request);var(session,customer,before)=await Resolve(http,owners,access,calculate:true,documents:true).ConfigureAwait(false);
            var input=await CustomerReviewHttp.ReadAsync<CalculateMonthlyEmiShare>(http).ConfigureAwait(false);if(!MonthlyEmiShareData.Request(input))throw new TenantBoundaryException("MONTHLY_EMI_SHARE_INVALID",400);
            var source=await client.Calculation(session,customer.CustomerId,input.MonthlyCashflowCalculationId,http.RequestAborted).ConfigureAwait(false);if(!Bound(source,customer.CustomerId))throw Invalid();
            var exact=new List<ReviewedLoanRecord>();foreach(var item in input.Loans)exact.Add(await loans.ReadAsync(session,customer.CustomerId,item.LoanRecordId,http.RequestAborted,item.Revision).ConfigureAwait(false));
            var receipt=await client.CalculateMonthlyEmiShare(session,customer.CustomerId,input,http.RequestAborted).ConfigureAwait(false);
            if(!MonthlyEmiShareData.Calculation(receipt,customer.CustomerId)||receipt.OperationId!=input.OperationId||receipt.RecordedBy!=session.ActorId||receipt.SourceMonthlyCashflow.CalculationId!=input.MonthlyCashflowCalculationId||receipt.ActiveLoansComplete!=input.ActiveLoansComplete||!MonthlyCashflowData.Same(receipt.SourceMonthlyCashflow,source)||!MonthlyCashflowData.Same(receipt.SourceLoans,exact))throw Invalid();
            var saved=await client.MonthlyEmiShare(session,customer.CustomerId,receipt.CalculationId,http.RequestAborted).ConfigureAwait(false);if(!MonthlyCashflowData.Same(saved,receipt))throw Invalid();
            await Confirm(http,owners,access,session,customer,before,calculate:true,documents:true).ConfigureAwait(false);return saved;
        });
        group.MapGet("/emi-shares/{calculationId}",async(string calculationId,HttpContext http,CustomerOwnerClient owners,TenantAccessClient access,ProductMonthlyCashflowClient client,ProductLoanRecordClient loans)=>
        {
            CustomerReviewHttp.NoQuery(http.Request);Identifier(calculationId);var(session,customer,before)=await Resolve(http,owners,access,calculate:true,documents:true).ConfigureAwait(false);
            var value=await client.MonthlyEmiShare(session,customer.CustomerId,calculationId,http.RequestAborted).ConfigureAwait(false);if(!MonthlyEmiShareData.Calculation(value,customer.CustomerId)||value.CalculationId!=calculationId)throw Invalid();
            var source=await client.Calculation(session,customer.CustomerId,value.SourceMonthlyCashflow.CalculationId,http.RequestAborted).ConfigureAwait(false);if(!MonthlyCashflowData.Same(source,value.SourceMonthlyCashflow))throw Invalid();
            foreach(var item in value.SourceLoans)if(!MonthlyCashflowData.Same(item,await loans.ReadAsync(session,customer.CustomerId,item.LoanRecordId,http.RequestAborted,item.Revision).ConfigureAwait(false)))throw Invalid();
            await Confirm(http,owners,access,session,customer,before,calculate:true,documents:true).ConfigureAwait(false);return value;
        });
        group.MapGet("/emi-shares",async(HttpContext http,CustomerOwnerClient owners,TenantAccessClient access,ProductMonthlyCashflowClient client,ProductLoanRecordClient loans)=>
        {
            var(session,customer,before)=await Resolve(http,owners,access,calculate:true,documents:true).ConfigureAwait(false);var cursor=CustomerReviewHttp.Cursor(http.Request);
            var page=await client.MonthlyEmiShares(session,customer.CustomerId,cursor,http.RequestAborted).ConfigureAwait(false);
            if(!Page(page.Items,page.HasMore,page.NextBeforeId,x=>MonthlyEmiShareData.Calculation(x,customer.CustomerId),x=>x.CalculationId)||page.Items.Any(x=>x.CalculationId==cursor))throw Invalid();
            foreach(var value in page.Items)foreach(var item in value.SourceLoans)if(!MonthlyCashflowData.Same(item,await loans.ReadAsync(session,customer.CustomerId,item.LoanRecordId,http.RequestAborted,item.Revision).ConfigureAwait(false)))throw Invalid();
            await Confirm(http,owners,access,session,customer,before,calculate:true,documents:true).ConfigureAwait(false);return page;
        });
    }
    public static bool Bound(MonthlyCashflowCalculation? value,string customer)=>MonthlyCashflowData.Calculation(value,customer);
    private static bool Page<T>(IReadOnlyList<T>? items,bool more,string? next,Func<T,bool> bound,Func<T,string> id)=>items is not null&&items.Count<=50&&items.All(x=>x is not null&&bound(x))&&items.Select(id).Distinct(StringComparer.Ordinal).Count()==items.Count&&(more?items.Count==50&&next==id(items[^1]):next is null);
    private static void Identifier(string id){if(!TenantAccessClient.Identifier(id,80))throw new TenantBoundaryException("MONTHLY_CASHFLOW_INVALID",400);}
    private static TenantBoundaryException Invalid()=>new("MONTHLY_CASHFLOW_RESPONSE_INVALID",503);
    private static async Task<(CustomerOwnerSession Session,CustomerOwnerWorkspace Customer,CustomerAccessDecision Decision)> Resolve(HttpContext http,CustomerOwnerClient owners,TenantAccessClient access,bool write=false,bool calculate=false,bool documents=false)
    {
        var session=await owners.SessionAsync(http).ConfigureAwait(false);var customer=await owners.OwnedCustomerAsync(session.TenantId,session.ActorId,http.RequestAborted).ConfigureAwait(false)??throw new TenantBoundaryException("PRODUCT_CUSTOMER_REQUIRED",403);
        var decision=await CustomerReviewHttp.AuthorizeAsync(http,customer.CustomerId,access,write:write,calculate:calculate,documents:documents).ConfigureAwait(false);
        if(decision.ActorId!=session.ActorId||decision.SubjectVersion!=session.SubjectVersion)throw new TenantBoundaryException("ACCESS_DENIED",403);return(session,customer,decision);
    }
    private static async Task Confirm(HttpContext http,CustomerOwnerClient owners,TenantAccessClient access,CustomerOwnerSession session,CustomerOwnerWorkspace customer,CustomerAccessDecision before,bool write=false,bool calculate=false,bool documents=false)
    {
        var (current,owned,after)=await Resolve(http,owners,access,write,calculate,documents).ConfigureAwait(false);CustomerReviewHttp.Confirm(before,after);
        if(current.TenantId!=session.TenantId||current.ActorId!=session.ActorId||current.SubjectVersion!=session.SubjectVersion||owned.CustomerId!=customer.CustomerId||owned.Version!=customer.Version)throw new TenantBoundaryException("MONTHLY_CASHFLOW_CONTEXT_CHANGED",409);
    }
}
public sealed class ProductMonthlyCashflowClient:IDisposable
{
    private readonly HttpClient client;private readonly Uri profile,rules;
    private static readonly JsonSerializerOptions Json=new(ContractJson.Options){RespectRequiredConstructorParameters=true,RespectNullableAnnotations=true,UnmappedMemberHandling=System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow};
    public ProductMonthlyCashflowClient(IConfiguration config):this(config,new HttpClient(new SocketsHttpHandler{AllowAutoRedirect=false,UseProxy=false})){}
    public ProductMonthlyCashflowClient(IConfiguration config,HttpClient client)
    {this.client=client;client.Timeout=TimeSpan.FromSeconds(30);client.MaxResponseContentBufferSize=33554432;profile=TenantAccessIntegration.LoopbackRoot(config["Monergy:Product:FinancialProfileUrl"]??throw new InvalidOperationException("Financial Profile endpoint required."));rules=TenantAccessIntegration.LoopbackRoot(config["Monergy:Product:FinancialRulesUrl"]??throw new InvalidOperationException("Financial Rules endpoint required."));}
    public Task<MonthlyCashflowComparison> Compare(CustomerOwnerSession s,string c,CompareMonthlyCashflow input,CancellationToken ct)=>Send<MonthlyCashflowComparison>(rules,s,c,"calculations/comparison",input,ct);
    public Task<ReviewedMonthlyCashflowPosition> SavePosition(CustomerOwnerSession s,string c,CreateMonthlyCashflowPosition input,CancellationToken ct)=>Send<ReviewedMonthlyCashflowPosition>(profile,s,c,"positions",input,ct);
    public Task<ReviewedMonthlyCashflowPosition> Position(CustomerOwnerSession s,string c,string id,CancellationToken ct)=>Send<ReviewedMonthlyCashflowPosition>(profile,s,c,"positions/"+Uri.EscapeDataString(id),null,ct);
    public Task<MonthlyCashflowPositionPage> Positions(CustomerOwnerSession s,string c,string? cursor,CancellationToken ct)=>Send<MonthlyCashflowPositionPage>(profile,s,c,"positions"+Cursor(cursor),null,ct);
    public Task<MonthlyCashflowCalculation> Calculate(CustomerOwnerSession s,string c,CalculateMonthlyCashflow input,CancellationToken ct)=>Send<MonthlyCashflowCalculation>(rules,s,c,"calculations",input,ct);
    public Task<MonthlyCashflowCalculation> Calculation(CustomerOwnerSession s,string c,string id,CancellationToken ct)=>Send<MonthlyCashflowCalculation>(rules,s,c,"calculations/"+Uri.EscapeDataString(id),null,ct);
    public Task<MonthlyCashflowCalculationPage> Calculations(CustomerOwnerSession s,string c,string? cursor,CancellationToken ct)=>Send<MonthlyCashflowCalculationPage>(rules,s,c,"calculations"+Cursor(cursor),null,ct);
    public Task<DebtServiceRatioCalculation> CalculateDebtServiceRatio(CustomerOwnerSession s,string c,CalculateDebtServiceRatio input,CancellationToken ct)=>Send<DebtServiceRatioCalculation>(rules,s,c,"debt-service-ratios",input,ct);
    public Task<DebtServiceRatioCalculation> DebtServiceRatio(CustomerOwnerSession s,string c,string id,CancellationToken ct)=>Send<DebtServiceRatioCalculation>(rules,s,c,"debt-service-ratios/"+Uri.EscapeDataString(id),null,ct);
    public Task<DebtServiceRatioCalculationPage> DebtServiceRatios(CustomerOwnerSession s,string c,string? cursor,CancellationToken ct)=>Send<DebtServiceRatioCalculationPage>(rules,s,c,"debt-service-ratios"+Cursor(cursor),null,ct);
    public Task<DebtToIncomeCalculation> CalculateDebtToIncome(CustomerOwnerSession s,string c,CalculateDebtToIncome input,CancellationToken ct)=>Send<DebtToIncomeCalculation>(rules,s,c,"debt-to-income-ratios",input,ct);
    public Task<DebtToIncomeCalculation> DebtToIncome(CustomerOwnerSession s,string c,string id,CancellationToken ct)=>Send<DebtToIncomeCalculation>(rules,s,c,"debt-to-income-ratios/"+Uri.EscapeDataString(id),null,ct);
    public Task<DebtToIncomeCalculationPage> DebtToIncomes(CustomerOwnerSession s,string c,string? cursor,CancellationToken ct)=>Send<DebtToIncomeCalculationPage>(rules,s,c,"debt-to-income-ratios"+Cursor(cursor),null,ct);
    public Task<MonthlyNetCashflowRateCalculation> CalculateMonthlyNetCashflowRate(CustomerOwnerSession s,string c,CalculateMonthlyNetCashflowRate input,CancellationToken ct)=>Send<MonthlyNetCashflowRateCalculation>(rules,s,c,"net-cashflow-rates",input,ct);
    public Task<MonthlyNetCashflowRateCalculation> MonthlyNetCashflowRate(CustomerOwnerSession s,string c,string id,CancellationToken ct)=>Send<MonthlyNetCashflowRateCalculation>(rules,s,c,"net-cashflow-rates/"+Uri.EscapeDataString(id),null,ct);
    public Task<MonthlyNetCashflowRateCalculationPage> MonthlyNetCashflowRates(CustomerOwnerSession s,string c,string? cursor,CancellationToken ct)=>Send<MonthlyNetCashflowRateCalculationPage>(rules,s,c,"net-cashflow-rates"+Cursor(cursor),null,ct);
    public Task<MonthlyEmiShareCalculation> CalculateMonthlyEmiShare(CustomerOwnerSession s,string c,CalculateMonthlyEmiShare input,CancellationToken ct)=>Send<MonthlyEmiShareCalculation>(rules,s,c,"emi-shares",input,ct);
    public Task<MonthlyEmiShareCalculation> MonthlyEmiShare(CustomerOwnerSession s,string c,string id,CancellationToken ct)=>Send<MonthlyEmiShareCalculation>(rules,s,c,"emi-shares/"+Uri.EscapeDataString(id),null,ct);
    public Task<MonthlyEmiShareCalculationPage> MonthlyEmiShares(CustomerOwnerSession s,string c,string? cursor,CancellationToken ct)=>Send<MonthlyEmiShareCalculationPage>(rules,s,c,"emi-shares"+Cursor(cursor),null,ct);
    private static string Cursor(string? value)=>value is null?"":"?beforeId="+Uri.EscapeDataString(value);
    private async Task<T> Send<T>(Uri root,CustomerOwnerSession session,string customer,string suffix,object? input,CancellationToken ct)
    {
        using var request=new HttpRequestMessage(input is null?HttpMethod.Get:HttpMethod.Post,new Uri(root,"local/v1/customers/"+Uri.EscapeDataString(customer)+"/monthly-cashflow/"+suffix));
        request.Headers.Add("X-Monergy-Tenant",session.TenantId);request.Headers.Add("X-Monergy-Session",session.AuthenticationContextId);if(input is not null)request.Content=JsonContent.Create(input,options:ContractJson.Options);
        using var response=await client.SendAsync(request,ct).ConfigureAwait(false);
        if(!response.IsSuccessStatusCode)throw new TenantBoundaryException("MONTHLY_CASHFLOW_UNAVAILABLE",(int)response.StatusCode is 400 or 401 or 403 or 404 or 409 or 413?(int)response.StatusCode:503);
        return await response.Content.ReadFromJsonAsync<T>(Json,ct).ConfigureAwait(false)??throw new TenantBoundaryException("MONTHLY_CASHFLOW_RESPONSE_INVALID",503);
    }
    public void Dispose()=>client.Dispose();
}
