using Microsoft.Extensions.Configuration;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.FinancialProfile.Application;
using Monergy.Services.FinancialProfile.Infrastructure;
using Monergy.UatGateway;

namespace Monergy.FinancialProfile.Tests;

public sealed class ReviewedBankAccountRecordTests
{
    private const string Customer="customer-bank-001";

    [Fact]
    public async Task CreateDerivesBalanceCurrencyAndDateFromExactCanonicalSource()
    {
        var(financial,source)=await Seed();var records=new InMemoryBankAccountRecords();
        var app=new BankAccountRecordsApplication(financial,records,TimeProvider.System);
        var input=Input(source.FinancialFactId);
        var saved=await app.CreateAsync("T001",Customer,"actor-001",input,default);
        Assert.Equal("Example Bank",saved.Account.BankName);Assert.Equal("Savings",saved.Account.AccountType);
        Assert.Equal("4821",saved.Account.AccountNumberLast4);Assert.Equal(2510.25m,saved.Balance);
        Assert.Equal("INR",saved.Currency);Assert.Equal(new DateOnly(2026,10,1),saved.BalanceAsOfDate);
        Assert.Equal(source.FinancialFactId,saved.Account.SourceFinancialFactId);Assert.Equal(1,saved.Account.SourceRevision);
        Assert.Equal(saved,await app.CreateAsync("T001",Customer,"actor-001",input,default));
    }

    [Theory]
    [InlineData("123")]
    [InlineData("12345")]
    [InlineData("12A4")]
    [InlineData("１２３４")]
    public async Task LastFourMustBeExactlyFourAsciiDigits(string last4)
    {
        var(financial,source)=await Seed();var records=new InMemoryBankAccountRecords();
        var app=new BankAccountRecordsApplication(financial,records,TimeProvider.System);
        var input=Input(source.FinancialFactId) with{Account=Input(source.FinancialFactId).Account with{AccountNumberLast4=last4}};
        var error=await Assert.ThrowsAsync<TenantBoundaryException>(()=>app.CreateAsync("T001",Customer,"actor-001",input,default));
        Assert.Equal(400,error.Status);Assert.Empty((await records.ListAsync("T001",Customer,default)).Items);
    }

    [Theory]
    [InlineData("INVESTMENT",Customer,1)]
    [InlineData("BANK_ACCOUNT","customer-foreign",1)]
    [InlineData("BANK_ACCOUNT",Customer,2)]
    public async Task WrongTypeForeignCustomerAndMissingRevisionFailClosed(string type,string sourceCustomer,int revision)
    {
        var(financial,source)=await Seed(type,sourceCustomer);var records=new InMemoryBankAccountRecords();
        var app=new BankAccountRecordsApplication(financial,records,TimeProvider.System);
        var input=Input(source.FinancialFactId) with{Account=Input(source.FinancialFactId).Account with{SourceRevision=revision}};
        var error=await Assert.ThrowsAsync<TenantBoundaryException>(()=>app.CreateAsync("T001",Customer,"actor-001",input,default));
        Assert.Equal(404,error.Status);Assert.Empty((await records.ListAsync("T001",Customer,default)).Items);
    }

    [Fact]
    public async Task CorrectionsAreAppendOnlyAndCannotChangeSourceLineageOrFinancialValues()
    {
        var(financial,source)=await Seed();var records=new InMemoryBankAccountRecords();
        var app=new BankAccountRecordsApplication(financial,records,TimeProvider.System);
        var saved=await app.CreateAsync("T001",Customer,"actor-001",Input(source.FinancialFactId),default);
        var correction=new ReviseBankAccountRecord(Guid.NewGuid().ToString("N"),1,
            saved.Account with{BankName="Renamed Bank",AccountType="Current",AccountNumberLast4="7314"});
        var revised=await app.ReviseAsync("T001",Customer,"actor-001",saved.BankAccountRecordId,correction,default);
        Assert.Equal(2,revised.Revision);Assert.Equal("Renamed Bank",revised.Account.BankName);
        Assert.Equal(saved.Balance,revised.Balance);Assert.Equal(saved.Currency,revised.Currency);
        Assert.Equal(saved.BalanceAsOfDate,revised.BalanceAsOfDate);Assert.Equal(saved.SourceProvenanceId,revised.SourceProvenanceId);
        Assert.Equal(revised,await app.ReviseAsync("T001",Customer,"actor-001",saved.BankAccountRecordId,correction,default));
        Assert.Equal([revised,saved],(await app.HistoryAsync("T001",Customer,saved.BankAccountRecordId,null,default)).Items);
        var changed=correction with{OperationId=Guid.NewGuid().ToString("N"),Account=correction.Account with{SourceRevision=2}};
        var immutable=await Assert.ThrowsAsync<TenantBoundaryException>(()=>app.ReviseAsync("T001",Customer,"actor-001",saved.BankAccountRecordId,changed,default));
        Assert.Equal("BANK_ACCOUNT_SOURCE_IMMUTABLE",immutable.Code);
        var stale=await Assert.ThrowsAsync<TenantBoundaryException>(()=>app.ReviseAsync("T001",Customer,"actor-001",saved.BankAccountRecordId,
            correction with{OperationId=Guid.NewGuid().ToString("N")},default));Assert.Equal("BANK_ACCOUNT_REVISION_CONFLICT",stale.Code);
    }

    [Fact]
    public async Task RecordsAreTenantAndCustomerIsolatedAndResponseContractIsBounded()
    {
        var(financial,source)=await Seed();var records=new InMemoryBankAccountRecords();
        var saved=await new BankAccountRecordsApplication(financial,records,TimeProvider.System)
            .CreateAsync("T001",Customer,"actor-001",Input(source.FinancialFactId),default);
        Assert.True(ProductBankAccountRecordEndpoints.Bound(saved,Customer));
        Assert.False(ProductBankAccountRecordEndpoints.Bound(saved with{CustomerId="foreign"},Customer));
        Assert.Empty((await records.ListAsync("T002",Customer,default)).Items);
        Assert.Null(await records.ReadAsync("T001","customer-foreign",saved.BankAccountRecordId,default));
    }

    [Fact]
    public void SuppliedGatewayClientIsNotMutated()
    {
        using var shared=new HttpClient{Timeout=TimeSpan.FromSeconds(77),MaxResponseContentBufferSize=2048};
        var configuration=new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string,string?>{{"Monergy:Product:FinancialProfileUrl","http://127.0.0.1:5001/"}}).Build();
        using var product=new ProductBankAccountRecordClient(configuration,shared);
        Assert.Equal(TimeSpan.FromSeconds(77),shared.Timeout);Assert.Equal(2048,shared.MaxResponseContentBufferSize);
    }

    private static CreateBankAccountRecord Input(string source)=>new(Guid.NewGuid().ToString("N"),
        new(" Example Bank "," Savings ","4821",source,1));

    private static async Task<(InMemoryFinancialProfileRepository Financial,AuthoritativeFinancialFact Source)> Seed(
        string type=FinancialObjectTypes.BankAccount,string customer=Customer)
    {
        var repository=new InMemoryFinancialProfileRepository();var app=new FinancialProfileApplication(repository,new CapturingTelemetry(),TimeProvider.System);
        var fact=new ValidatedSourceFact("source-bank-account",customer,type,"Account balance",2510.25m,"INR",new(2026,10,1),
            "line 1",1m,"evidence-bank","document-bank","fixture-extractor/1.0.0","validation/1.0.0");
        var normalized=await app.NormalizeSourceFactsAsync(FinancialProfileTestContext.Request(Vs02ContractNames.NormalizeSourceFacts,
            new NormalizeSourceFacts("profile-"+customer,customer,"processing-bank",[fact],"normalize/1.0.0",DateTimeOffset.UtcNow),
            "request-bank", "key-bank",FinancialProfileTestContext.Security(customer)));
        var id=normalized.Data!.Facts.Single().FinancialFactId;
        var source=await app.GetFinancialFactAsync(FinancialProfileTestContext.Request(Vs02ContractNames.GetFinancialFact,
            new GetFinancialFact(id,customer,1),"read-bank",security:FinancialProfileTestContext.Security(customer)));
        return(repository,source.Data!);
    }
}
