using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Monergy.Contracts;
using Monergy.Platform;
namespace Monergy.Services.FinancialProfile.Application;
public interface IMonthlyCashflowPositionRepository
{
    Task<ReviewedMonthlyCashflowPosition> CreateAsync(string tenant,string customer,string actor,string fingerprint,ReviewedMonthlyCashflowPosition value,CancellationToken ct);
    Task<ReviewedMonthlyCashflowPosition?> ReadAsync(string tenant,string customer,string id,CancellationToken ct);
    Task<MonthlyCashflowPositionPage> ListAsync(string tenant,string customer,string? beforeId,CancellationToken ct);
}
public sealed class MonthlyCashflowPositionsApplication(IMonthlyCashflowPositionRepository repository,IIncomeRecordRepository incomes,IExpenseRecordRepository expenses,TimeProvider clock)
{
    public async Task<ReviewedMonthlyCashflowPosition> CreateAsync(string tenant,string customer,string actor,CreateMonthlyCashflowPosition input,CancellationToken ct)
    {
        if(input is null||!MonthlyCashflowData.Operation(input.OperationId)||!MonthlyCashflowData.Draft(input.Position,clock.GetUtcNow()))throw new TenantBoundaryException("MONTHLY_CASHFLOW_POSITION_INVALID",400);
        if(input.Position.ReplacesPositionId is {} previous)await ReadAsync(tenant,customer,previous,ct).ConfigureAwait(false);
        var lines=new List<MonthlyCashflowPositionLine>();
        foreach(var line in input.Position.Lines)lines.Add(await ResolveLineAsync(tenant,customer,input.Position.AsOfDate,line,ct).ConfigureAwait(false));
        var fingerprint=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(input,ContractJson.Options)));
        var id="cashflow-position-"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture,$"{tenant}|{customer}|{actor}|{input.OperationId}")))).ToLowerInvariant()[..60];
        return await repository.CreateAsync(tenant,customer,actor,fingerprint,new(id,input.OperationId,customer,input.Position,lines,actor,clock.GetUtcNow()),ct).ConfigureAwait(false);
    }
    public async Task<ReviewedMonthlyCashflowPosition> ReadAsync(string tenant,string customer,string id,CancellationToken ct)
    {
        if(!TenantAccessClient.Identifier(id,80))throw new TenantBoundaryException("MONTHLY_CASHFLOW_POSITION_INVALID",400);
        return await repository.ReadAsync(tenant,customer,id,ct).ConfigureAwait(false)??throw new TenantBoundaryException("MONTHLY_CASHFLOW_POSITION_NOT_FOUND",404);
    }
    public Task<MonthlyCashflowPositionPage> ListAsync(string tenant,string customer,string? beforeId,CancellationToken ct)
    {
        if(beforeId is not null&&!TenantAccessClient.Identifier(beforeId,80))throw new TenantBoundaryException("MONTHLY_CASHFLOW_POSITION_INVALID",400);
        return repository.ListAsync(tenant,customer,beforeId,ct);
    }
    private async Task<MonthlyCashflowPositionLine> ResolveLineAsync(string tenant,string customer,DateOnly monthEnd,MonthlyCashflowLineDraft line,CancellationToken ct)
    {
        if(line.Record is not {} reference)return new(line,null);
        string? name=null,kind=null,currency=null,amount=null;DateOnly? start=null,end=null;MonthlyCashflowLineSource? source=null;
        if(reference.Kind=="INCOME")
        {
            var row=await incomes.ReadAsync(tenant,customer,reference.RecordId,ct,reference.Revision).ConfigureAwait(false);
            if(row is {VerificationStatus:"CUSTOMER_REVIEWED"}&&row.CustomerId==customer&&row.Revision==reference.Revision&&row.IncomeRecordId==reference.RecordId)
            {name=row.Income.Name;kind=row.Income.IncomeType;currency=row.Currency;amount=Money(row.Income.Amount);start=row.Income.PeriodStart;end=row.Income.PeriodEnd;source=new(row.Income.SourceFinancialFactId,row.Income.SourceRevision,row.SourceProvenanceId,row.DocumentVersionId,row.EvidenceId,row.RecordedBy,row.RecordedAt);}
        }
        else
        {
            var row=await expenses.ReadAsync(tenant,customer,reference.RecordId,ct,reference.Revision).ConfigureAwait(false);
            if(row is {VerificationStatus:"CUSTOMER_REVIEWED"}&&row.CustomerId==customer&&row.Revision==reference.Revision&&row.ExpenseRecordId==reference.RecordId)
            {name=row.Expense.Name;kind=row.Expense.Category;currency=row.Currency;amount=Money(row.Expense.Amount);start=row.Expense.PeriodStart;end=row.Expense.PeriodEnd;source=new(row.Expense.SourceFinancialFactId,row.Expense.SourceRevision,row.SourceProvenanceId,row.DocumentVersionId,row.EvidenceId,row.RecordedBy,row.RecordedAt);}
        }
        if(source is null)throw new TenantBoundaryException("MONTHLY_CASHFLOW_SOURCE_NOT_FOUND",404);
        if(line.Name!=name||line.Kind!=kind||line.Currency!=currency||line.Amount!=amount||line.ValueDate!=monthEnd||end!=monthEnd||start!=new DateOnly(monthEnd.Year,monthEnd.Month,1))throw new TenantBoundaryException("MONTHLY_CASHFLOW_SOURCE_MISMATCH",409);
        return new(line,source);
    }
    private static string Money(decimal value)=>value.ToString("F2",CultureInfo.InvariantCulture);
}
