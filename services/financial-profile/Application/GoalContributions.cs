using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Monergy.Contracts;
using Monergy.Platform;
namespace Monergy.Services.FinancialProfile.Application;

public interface IGoalContributionRepository
{
    Task<ReportedGoalContribution> CreateAsync(string tenant, string customer, string actor,
        string fingerprint, ReportedGoalContribution record, CancellationToken ct);
    Task<ReportedGoalContribution?> ReadAsync(string tenant, string customer, string id, CancellationToken ct, int? revision = null);
    Task<GoalContributionList> ListAsync(string tenant, string customer,string financialGoalId, CancellationToken ct);
    Task<ReportedGoalContribution> ReviseAsync(string tenant, string customer, string actor, string fingerprint,
        int expectedRevision, ReportedGoalContribution record, CancellationToken ct);
    Task<GoalContributionHistory> HistoryAsync(string tenant, string customer, string id, int? beforeRevision, CancellationToken ct);
}

public sealed class GoalContributionsApplication(IFinancialGoalRepository goals,IGoalContributionRepository repository,TimeProvider clock)
{
    public static bool Valid(CreateGoalContribution? input,DateTimeOffset now)=>input?.Contribution is {} item &&
        Guid.TryParseExact(input.OperationId,"N",out _) && !input.OperationId.Any(char.IsAsciiLetterUpper) &&
        TenantAccessClient.Identifier(item.FinancialGoalId,80) && item.GoalRevision>0 &&
        item.Amount is >0 and <=999999999999.99m && decimal.Round(item.Amount,2)==item.Amount &&
        item.ContributionDate.Year>=1900 && item.ContributionDate<=DateOnly.FromDateTime(now.UtcDateTime.AddHours(14));
    public async Task<ReportedGoalContribution> CreateAsync(string tenant,string customer,string actor,CreateGoalContribution input,CancellationToken ct)
    {
        if(!Valid(input,clock.GetUtcNow()))throw new TenantBoundaryException("GOAL_CONTRIBUTION_INVALID",400);
        var goal=await SourceAsync(tenant,customer,input.Contribution,ct).ConfigureAwait(false);
        var fingerprint=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(input,ContractJson.Options)));
        var id="contribution-"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Create(CultureInfo.InvariantCulture,$"{tenant}|{customer}|{actor}|{input.OperationId}")))).ToLowerInvariant();
        return await repository.CreateAsync(tenant,customer,actor,fingerprint,new(id,input.OperationId,customer,input.Contribution,goal.Goal,actor,clock.GetUtcNow(),"CUSTOMER_REPORTED"),ct).ConfigureAwait(false);
    }
    public async Task<GoalContributionList> ListAsync(string tenant,string customer,string financialGoalId,CancellationToken ct)
    {
        if(!TenantAccessClient.Identifier(financialGoalId,80))throw new TenantBoundaryException("GOAL_CONTRIBUTION_INVALID",400);
        if(await goals.ReadAsync(tenant,customer,financialGoalId,ct).ConfigureAwait(false) is null)throw new TenantBoundaryException("GOAL_CONTRIBUTION_GOAL_NOT_FOUND",404);
        return await repository.ListAsync(tenant,customer,financialGoalId,ct).ConfigureAwait(false);
    }
    public async Task<ReportedGoalContribution> ReviseAsync(string tenant,string customer,string actor,string id,ReviseGoalContribution input,CancellationToken ct)
    {
        if(input is null||input.ExpectedRevision is <1 or int.MaxValue||!Valid(new(input.OperationId,input.Contribution),clock.GetUtcNow()))throw new TenantBoundaryException("GOAL_CONTRIBUTION_INVALID",400);
        var original=await ReadAsync(tenant,customer,id,ct,1).ConfigureAwait(false);
        if(input.Contribution.FinancialGoalId!=original.Contribution.FinancialGoalId||input.Contribution.GoalRevision!=original.Contribution.GoalRevision)throw new TenantBoundaryException("GOAL_CONTRIBUTION_SOURCE_IMMUTABLE",400);
        var goal=await SourceAsync(tenant,customer,input.Contribution,ct).ConfigureAwait(false);
        if(goal.Goal!=original.SourceGoal)throw new TenantBoundaryException("GOAL_CONTRIBUTION_SOURCE_UNAVAILABLE",503);
        var fingerprint=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(input,ContractJson.Options)));
        var record=original with {OperationId=input.OperationId,Contribution=input.Contribution,RecordedBy=actor,RecordedAt=clock.GetUtcNow(),Revision=input.ExpectedRevision+1};
        return await repository.ReviseAsync(tenant,customer,actor,fingerprint,input.ExpectedRevision,record,ct).ConfigureAwait(false);
    }
    public static bool ValidWithdrawal(WithdrawGoalContribution? input)=>input is not null &&
        Guid.TryParseExact(input.OperationId,"N",out _) && !input.OperationId.Any(char.IsAsciiLetterUpper) &&
        input.ExpectedRevision is >0 and <int.MaxValue && !string.IsNullOrWhiteSpace(input.Reason) &&
        input.Reason.Length<=500 && input.Reason==input.Reason.Trim() && !input.Reason.Any(char.IsControl);
    public async Task<ReportedGoalContribution> WithdrawAsync(string tenant,string customer,string actor,string id,WithdrawGoalContribution input,CancellationToken ct)
    {
        if(!ValidWithdrawal(input))throw new TenantBoundaryException("GOAL_CONTRIBUTION_INVALID",400);
        var prior=await ReadAsync(tenant,customer,id,ct,input.ExpectedRevision).ConfigureAwait(false);
        if(prior.DeclarationStatus!="CUSTOMER_REPORTED")throw new TenantBoundaryException("GOAL_CONTRIBUTION_WITHDRAWN",409);
        var fingerprint=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(input,ContractJson.Options)));
        var record=prior with {OperationId=input.OperationId,RecordedBy=actor,RecordedAt=clock.GetUtcNow(),
            Revision=input.ExpectedRevision+1,DeclarationStatus="CUSTOMER_WITHDRAWN",WithdrawalReason=input.Reason};
        return await repository.ReviseAsync(tenant,customer,actor,fingerprint,input.ExpectedRevision,record,ct).ConfigureAwait(false);
    }
    public async Task<ReportedGoalContribution> ReadAsync(string tenant,string customer,string id,CancellationToken ct,int? revision=null)
    {
        if(!TenantAccessClient.Identifier(id,80)||revision is <1)throw new TenantBoundaryException("GOAL_CONTRIBUTION_INVALID",400);
        return await repository.ReadAsync(tenant,customer,id,ct,revision).ConfigureAwait(false)??throw new TenantBoundaryException("GOAL_CONTRIBUTION_NOT_FOUND",404);
    }
    public async Task<GoalContributionHistory> HistoryAsync(string tenant,string customer,string id,int? beforeRevision,CancellationToken ct)
    {
        if(beforeRevision is <1)throw new TenantBoundaryException("GOAL_CONTRIBUTION_INVALID",400);
        await ReadAsync(tenant,customer,id,ct,1).ConfigureAwait(false);
        return await repository.HistoryAsync(tenant,customer,id,beforeRevision,ct).ConfigureAwait(false);
    }
    private async Task<CustomerFinancialGoal> SourceAsync(string tenant,string customer,GoalContributionDraft input,CancellationToken ct)
    {
        var goal=await goals.ReadAsync(tenant,customer,input.FinancialGoalId,ct,input.GoalRevision).ConfigureAwait(false);
        return goal is {DeclarationStatus:"CUSTOMER_DEFINED"}&&goal.CustomerId==customer&&goal.FinancialGoalId==input.FinancialGoalId&&goal.Revision==input.GoalRevision
            ?goal:throw new TenantBoundaryException("GOAL_CONTRIBUTION_GOAL_NOT_FOUND",404);
    }
}
