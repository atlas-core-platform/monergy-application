using Microsoft.Extensions.Configuration;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.FinancialProfile.Application;
using Monergy.Services.FinancialProfile.Infrastructure;
using Monergy.UatGateway;
using Xunit;

namespace Monergy.FinancialProfile.Tests;

public sealed class ReviewedCreditCardRecordTests
{
    private const string Customer="customer-card-001";

    [Fact]
    public async Task CreateDerivesBalanceCurrencyAndDateFromExactCanonicalSource()
    {
        var(financial,source)=await Seed();var records=new InMemoryCreditCardRecords();
        var app=new CreditCardRecordsApplication(financial,records,TimeProvider.System);
        var input=Input(source.FinancialFactId);
        var saved=await app.CreateAsync("T001",Customer,"actor-001",input,default);
        Assert.Equal("Example Issuer",saved.Card.IssuerName);Assert.Equal("Rewards",saved.Card.CardType);
        Assert.Equal("4821",saved.Card.CardNumberLast4);Assert.Equal(2510.25m,saved.Balance);
        Assert.Equal("INR",saved.Currency);Assert.Equal(new DateOnly(2026,10,1),saved.BalanceAsOfDate);
        Assert.Equal(source.FinancialFactId,saved.Card.SourceFinancialFactId);Assert.Equal(1,saved.Card.SourceRevision);
        Assert.Equal(saved,await app.CreateAsync("T001",Customer,"actor-001",input,default));
    }

    [Theory]
    [InlineData("123")]
    [InlineData("12345")]
    [InlineData("12A4")]
    [InlineData("１２３４")]
    public async Task LastFourMustBeExactlyFourAsciiDigits(string last4)
    {
        var(financial,source)=await Seed();var records=new InMemoryCreditCardRecords();
        var app=new CreditCardRecordsApplication(financial,records,TimeProvider.System);
        var input=Input(source.FinancialFactId) with{Card=Input(source.FinancialFactId).Card with{CardNumberLast4=last4}};
        var error=await Assert.ThrowsAsync<TenantBoundaryException>(()=>app.CreateAsync("T001",Customer,"actor-001",input,default));
        Assert.Equal(400,error.Status);Assert.Empty((await records.ListAsync("T001",Customer,default)).Items);
    }

    [Theory]
    [InlineData("INVESTMENT",Customer,1)]
    [InlineData("LOAN","customer-foreign",1)]
    [InlineData("LOAN",Customer,2)]
    public async Task WrongTypeForeignCustomerAndMissingRevisionFailClosed(string type,string sourceCustomer,int revision)
    {
        var(financial,source)=await Seed(type,sourceCustomer);var records=new InMemoryCreditCardRecords();
        var app=new CreditCardRecordsApplication(financial,records,TimeProvider.System);
        var input=Input(source.FinancialFactId) with{Card=Input(source.FinancialFactId).Card with{SourceRevision=revision}};
        var error=await Assert.ThrowsAsync<TenantBoundaryException>(()=>app.CreateAsync("T001",Customer,"actor-001",input,default));
        Assert.Equal(404,error.Status);Assert.Empty((await records.ListAsync("T001",Customer,default)).Items);
    }

    [Fact]
    public async Task CorrectionsAreAppendOnlyAndCannotChangeSourceLineageOrFinancialValues()
    {
        var(financial,source)=await Seed();var records=new InMemoryCreditCardRecords();
        var app=new CreditCardRecordsApplication(financial,records,TimeProvider.System);
        var saved=await app.CreateAsync("T001",Customer,"actor-001",Input(source.FinancialFactId),default);
        var correction=new ReviseCreditCardRecord(Guid.NewGuid().ToString("N"),1,
            saved.Card with{IssuerName="Renamed Issuer",CardType="Travel",CardNumberLast4="7314"});
        var revised=await app.ReviseAsync("T001",Customer,"actor-001",saved.CreditCardRecordId,correction,default);
        Assert.Equal(2,revised.Revision);Assert.Equal("Renamed Issuer",revised.Card.IssuerName);
        Assert.Equal(saved.Balance,revised.Balance);Assert.Equal(saved.Currency,revised.Currency);
        Assert.Equal(saved.BalanceAsOfDate,revised.BalanceAsOfDate);Assert.Equal(saved.SourceProvenanceId,revised.SourceProvenanceId);
        Assert.Equal(revised,await app.ReviseAsync("T001",Customer,"actor-001",saved.CreditCardRecordId,correction,default));
        Assert.Equal([revised,saved],(await app.HistoryAsync("T001",Customer,saved.CreditCardRecordId,null,default)).Items);
        var changed=correction with{OperationId=Guid.NewGuid().ToString("N"),Card=correction.Card with{SourceRevision=2}};
        var immutable=await Assert.ThrowsAsync<TenantBoundaryException>(()=>app.ReviseAsync("T001",Customer,"actor-001",saved.CreditCardRecordId,changed,default));
        Assert.Equal("CREDIT_CARD_SOURCE_IMMUTABLE",immutable.Code);
        var stale=await Assert.ThrowsAsync<TenantBoundaryException>(()=>app.ReviseAsync("T001",Customer,"actor-001",saved.CreditCardRecordId,
            correction with{OperationId=Guid.NewGuid().ToString("N")},default));Assert.Equal("CREDIT_CARD_REVISION_CONFLICT",stale.Code);
    }

    [Fact]
    public async Task RecordsAreTenantAndCustomerIsolatedAndResponseContractIsBounded()
    {
        var(financial,source)=await Seed();var records=new InMemoryCreditCardRecords();
        var saved=await new CreditCardRecordsApplication(financial,records,TimeProvider.System)
            .CreateAsync("T001",Customer,"actor-001",Input(source.FinancialFactId),default);
        Assert.True(ProductCreditCardRecordEndpoints.Bound(saved,Customer));
        Assert.False(ProductCreditCardRecordEndpoints.Bound(saved with{CustomerId="foreign"},Customer));
        Assert.Empty((await records.ListAsync("T002",Customer,default)).Items);
        Assert.Null(await records.ReadAsync("T001","customer-foreign",saved.CreditCardRecordId,default));
    }

    [Fact]
    public void SuppliedGatewayClientIsNotMutated()
    {
        using var shared=new HttpClient{Timeout=TimeSpan.FromSeconds(77),MaxResponseContentBufferSize=2048};
        var configuration=new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string,string?>{{"Monergy:Product:FinancialProfileUrl","http://127.0.0.1:5001/"}}).Build();
        using var product=new ProductCreditCardRecordClient(configuration,shared);
        Assert.Equal(TimeSpan.FromSeconds(77),shared.Timeout);Assert.Equal(2048,shared.MaxResponseContentBufferSize);
    }

    private static CreateCreditCardRecord Input(string source)=>new(Guid.NewGuid().ToString("N"),
        new(" Example Issuer "," Rewards ","4821",source,1));

    private static async Task<(InMemoryFinancialProfileRepository Financial,AuthoritativeFinancialFact Source)> Seed(
        string type=FinancialObjectTypes.Loan,string customer=Customer)
    {
        var repository=new InMemoryFinancialProfileRepository();var app=new FinancialProfileApplication(repository,new CapturingTelemetry(),TimeProvider.System);
        var fact=new ValidatedSourceFact("source-credit-card",customer,type,"Card balance",2510.25m,"INR",new(2026,10,1),
            "line 1",1m,"evidence-card","document-card","fixture-extractor/1.0.0","validation/1.0.0");
        var normalized=await app.NormalizeSourceFactsAsync(FinancialProfileTestContext.Request(Vs02ContractNames.NormalizeSourceFacts,
            new NormalizeSourceFacts("profile-"+customer,customer,"processing-card",[fact],"normalize/1.0.0",DateTimeOffset.UtcNow),
            "request-card", "key-card",FinancialProfileTestContext.Security(customer)));
        var id=normalized.Data!.Facts.Single().FinancialFactId;
        var source=await app.GetFinancialFactAsync(FinancialProfileTestContext.Request(Vs02ContractNames.GetFinancialFact,
            new GetFinancialFact(id,customer,1),"read-card",security:FinancialProfileTestContext.Security(customer)));
        return(repository,source.Data!);
    }
}
