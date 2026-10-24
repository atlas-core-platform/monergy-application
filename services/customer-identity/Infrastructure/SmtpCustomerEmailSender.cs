using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using Monergy.Contracts;
using Monergy.Services.CustomerIdentity.Application;

namespace Monergy.Services.CustomerIdentity.Infrastructure;

public sealed record CustomerEmailDeliveryOptions(string Host, int Port, string From, string User, string Password,
    string[] Tenants, string? LocalPersonaTestInbox = null)
{
    public static CustomerEmailDeliveryOptions Read(IConfiguration configuration)
    {
        const string prefix = "Monergy:CustomerEmailAccess:";
        var host = configuration[prefix + "SmtpHost"] ?? "";
        var port = configuration.GetValue<int?>(prefix + "SmtpPort") ?? 587;
        var from = CustomerEmailAccess.NormalizeEmail(configuration[prefix + "From"]);
        var user = configuration[prefix + "SmtpUser"] ?? "";
        var password = configuration[prefix + "SmtpPassword"] ?? "";
        var tenants = configuration.GetSection("Monergy:AccessIntegration:CustomerIdentityDatabases").GetChildren().Select(x => x.Key).ToArray();
        if (Uri.CheckHostName(host) == UriHostNameType.Unknown || host.Length > 253 || port is < 1 or > 65535 ||
            string.IsNullOrWhiteSpace(user) || user.Length > 320 || user.Any(char.IsControl) ||
            string.IsNullOrEmpty(password) || tenants.Length is < 1 or > 100)
            throw new InvalidOperationException("Customer email delivery configuration is incomplete.");
        var testInboxRaw = configuration[prefix + "LocalPersonaTestInbox"];
        string? testInbox = null;
        if (!string.IsNullOrWhiteSpace(testInboxRaw))
        {
            // Deliberate LOCAL-only exception for the fixed A100/A200/A300/A900
            // reference personas. It is NOT email ownership proof for production.
            if (configuration["Monergy:ExecutionZone"] != "LOCAL" ||
                configuration["Monergy:PersonaCredentials:Enabled"] != "true" ||
                !host.Equals("smtp.gmail.com", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Persona test mailbox redirection is LOCAL-only with Gmail SMTP.");
            testInbox = CustomerEmailAccess.NormalizeEmail(testInboxRaw);
            if (!testInbox.EndsWith("@gmail.com", StringComparison.OrdinalIgnoreCase) &&
                !testInbox.EndsWith("@googlemail.com", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("LOCAL Gmail test inbox requires a Gmail recipient.");
        }
        return new(host, port, from, user, password, tenants, testInbox);
    }
}

public sealed class SmtpCustomerEmailSender(CustomerEmailDeliveryOptions options) : ICustomerEmailSender
{
    public static string ResolveRecipient(string email, string? localTestInbox)
    {
        if (localTestInbox is null) return email;
        // Match ONLY the local bootstrap references. Imported user addresses
        // are always delivered to their actual registered email.
        return email is "a100@example.test" or "a200@example.test" or
            "a300@example.test" or "a900@example.test"
            ? localTestInbox : email;
    }
    public async Task SendAsync(string email, string code, CancellationToken ct)
    {
        var recipient = ResolveRecipient(email, options.LocalPersonaTestInbox);
        using var message = new MailMessage(options.From, recipient)
        {
            Subject = "Your Monergy verification code",
            Body = $"Your Monergy verification code is {code}. It expires five minutes after your request and can be used once. If you did not request this code, ignore this email." +
                (recipient != email ? $"\\nLOCAL UAT reference identity: {email}" : ""),
            IsBodyHtml = false,
        };
        using var client = new SmtpClient(options.Host, options.Port)
        {
            EnableSsl = true,
            UseDefaultCredentials = false,
            Credentials = new NetworkCredential(options.User, options.Password),
        };
        await client.SendMailAsync(message, ct).ConfigureAwait(false);
    }
}

public sealed class CustomerEmailDelivery(
    ICustomerEmailAccessRepository repository,
    CustomerEmailCodeProtector protector,
    ICustomerEmailSender sender,
    CustomerEmailDeliveryOptions options,
    TimeProvider clock,
    ILogger<CustomerEmailDelivery> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> Waiting = LoggerMessage.Define(LogLevel.Warning,
        new EventId(4301, "CustomerEmailRepositoryUnavailable"), "Customer email delivery is waiting for its owner repository.");
    public async Task DeliverPendingAsync(CancellationToken ct)
    {
        foreach (var tenant in options.Tenants)
        {
            var pending = await repository.LeaseAsync(tenant, clock.GetUtcNow(), ct).ConfigureAwait(false);
            if (pending is null) continue;
            var sent = false;
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(TimeSpan.FromSeconds(10));
                await sender.SendAsync(pending.Email, protector.Unprotect(tenant, pending.ChallengeHash, pending.ProtectedCode), deadline.Token).ConfigureAwait(false);
                sent = true;
            }
            catch (Exception failure) when (failure is SmtpException or CryptographicException or OperationCanceledException)
            {
                // SMTP error strings/exception details can contain recipient data.
                // Never put a code, encrypted payload, address or credential in logs.
                if (ct.IsCancellationRequested) throw;
            }
            await repository.CompleteDeliveryAsync(tenant, pending.ChallengeHash, pending.DeliveryAttempt, sent, clock.GetUtcNow(), ct).ConfigureAwait(false);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try { await DeliverPendingAsync(stoppingToken).ConfigureAwait(false); }
            catch (Exception failure) when (failure is System.Data.Common.DbException or TenantAccessException)
            {
                Waiting(logger, null);
            }
        }
    }
}
