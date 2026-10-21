using System.Net.Mail;
using System.Security.Cryptography;
using Dapper;
using Monergy.Contracts;
using Monergy.Services.CustomerIdentity.Application;
namespace Monergy.Services.CustomerIdentity.Infrastructure;
public sealed class CustomerRegistrationDelivery(PostgresTenantSessions sessions,CustomerEmailDeliveryOptions options,
    CustomerEmailCodeProtector protector,ICustomerEmailSender sender):BackgroundService
{
    public async Task DeliverAsync(CancellationToken ct)
    {
        foreach(var tenant in options.Tenants)
        {
            await using var db=await sessions.OpenAsync(tenant,ct);
            await db.ExecuteAsync(new CommandDefinition("UPDATE customer_identity.registration_challenges SET protected_code=NULL WHERE consumed OR attempts>=5 OR expires_at<=clock_timestamp() OR delivery_attempt>=3;",cancellationToken:ct));
            var row=await db.QuerySingleOrDefaultAsync<Delivery>(new CommandDefinition("""
            UPDATE customer_identity.registration_challenges SET lease_until=clock_timestamp()+interval '20 seconds',delivery_attempt=delivery_attempt+1
            WHERE challenge_hash=(SELECT challenge_hash FROM customer_identity.registration_challenges WHERE protected_code IS NOT NULL
             AND NOT consumed AND NOT delivered AND delivery_attempt<3 AND expires_at>clock_timestamp()
             AND (lease_until IS NULL OR lease_until<=clock_timestamp()) ORDER BY requested_at FOR UPDATE SKIP LOCKED LIMIT 1)
            RETURNING purpose AS Purpose,email AS Email,challenge_hash AS Hash,protected_code AS Code,delivery_attempt AS Attempt;
            """,cancellationToken:ct));
            if(row is null)continue;var sent=false;
            try
            {
                using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromSeconds(10));
                await sender.SendAsync(row.Email,protector.Unprotect(tenant+":"+row.Purpose,row.Hash,row.Code),deadline.Token);sent=true;
            }
            catch(Exception e)when(e is SmtpException or CryptographicException or OperationCanceledException){if(ct.IsCancellationRequested)throw;}
            await db.ExecuteAsync(new CommandDefinition("""
            UPDATE customer_identity.registration_challenges SET delivered=@Sent,
             protected_code=CASE WHEN @Sent OR delivery_attempt>=3 THEN NULL ELSE protected_code END
            WHERE challenge_hash=@Hash AND delivery_attempt=@Attempt;
            """,new{row.Hash,row.Attempt,Sent=sent},cancellationToken:ct));
        }
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer=new PeriodicTimer(TimeSpan.FromSeconds(2));
        while(await timer.WaitForNextTickAsync(stoppingToken))
        {
            try{await DeliverAsync(stoppingToken);}
            catch(Exception e)when(e is System.Data.Common.DbException or TenantAccessException){/* Retry without logging recipient or credential data. */}
        }
    }
    private sealed record Delivery(string Purpose,string Email,string Hash,byte[] Code,int Attempt);
}
