using Monergy.Platform;
namespace Monergy.Services.CustomerIdentity.Application;

public sealed record AdminCustomer(string TenantId,string CustomerId,string DisplayName,string? Email,string? SecondaryLabel,bool Active,long Version,bool OwnerLinked,bool ActivationAvailable);
public sealed record AdminCustomerPage(string TenantId,IReadOnlyList<AdminCustomer> Items,string? NextCursor,long Total,long Active,long Inactive,long Matching);
public sealed record AdminCustomerQuery(string Search="",string Status="all",string? After=null);
public sealed record CustomerStatusChange(string OperationId,long ExpectedVersion,bool Active,string Reason);
public sealed record CustomerStatusReceipt(string TenantId,string CustomerId,string OperationId,AdminCustomer Before,AdminCustomer After,string Reason,string RecordedBy,string RecordedByLabel,DateTimeOffset RecordedAt);
public sealed record CustomerStatusHistory(string TenantId,string CustomerId,IReadOnlyList<CustomerStatusReceipt> Items,long? NextBeforeVersion);
public interface ICustomerAdministrationRepository
{
 Task<AdminCustomerPage> ListAsync(string tenant,AdminCustomerQuery query,CancellationToken ct);
 Task<AdminCustomer?> ReadAsync(string tenant,string customer,CancellationToken ct);
 Task<CustomerStatusReceipt> ChangeAsync(string tenant,string actor,string customer,CustomerStatusChange change,DateTimeOffset now,CancellationToken ct);
 Task<CustomerStatusReceipt?> ReceiptAsync(string tenant,string customer,string operation,CancellationToken ct);
 Task<CustomerStatusHistory> HistoryAsync(string tenant,string customer,long? before,CancellationToken ct);
}
public static class CustomerAdministration
{
 public const int PageSize=50;
 public static void Query(AdminCustomerQuery q)
 {if(q.Search!=q.Search.Trim()||q.Search.Length>120||q.Search.Any(char.IsControl)||q.Status is not ("all" or "active" or "inactive")||q.After is not null&&!TenantAccessClient.Identifier(q.After,128))throw new TenantBoundaryException("CUSTOMER_QUERY_INVALID",400);}
 public static void Change(CustomerStatusChange c)
 {if(!Guid.TryParseExact(c.OperationId,"N",out _)||c.OperationId.Any(x=>x is >='A' and <='F')||c.ExpectedVersion is <1 or >=9007199254740991||string.IsNullOrWhiteSpace(c.Reason)||c.Reason!=c.Reason.Trim()||c.Reason.Length>500||c.Reason.Any(char.IsControl))throw new TenantBoundaryException("CUSTOMER_CHANGE_INVALID",400);}
 public static void Confirm(TenantAdminContext a,TenantAdminContext b)
 {if(a!=b)throw new TenantBoundaryException("ADMINISTRATOR_CONTEXT_CHANGED",409);}
 public static void Receipt(CustomerStatusReceipt r,string tenant,string actor,string id,CustomerStatusChange c)
 {if(r.TenantId!=tenant||r.CustomerId!=id||r.OperationId!=c.OperationId||r.RecordedBy!=actor||r.Reason!=c.Reason||r.Before.TenantId!=tenant||r.After.TenantId!=tenant||r.Before.CustomerId!=id||r.After.CustomerId!=id||r.Before.Version!=c.ExpectedVersion||r.After.Version!=c.ExpectedVersion+1||r.Before.Active==c.Active||r.After.Active!=c.Active||r.After!=(r.Before with{Active=c.Active,Version=c.ExpectedVersion+1})||r.RecordedAt>DateTimeOffset.UtcNow.AddSeconds(5))throw new TenantBoundaryException("CUSTOMER_RECEIPT_INVALID",503);}
}
