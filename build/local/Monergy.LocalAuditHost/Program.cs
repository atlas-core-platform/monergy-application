using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.Audit;
using Monergy.Services.Audit.Application;
using Monergy.LocalAuditHost;

const string profile = "persisted-reporting";
var builder = WebApplication.CreateBuilder(args);
builder.AddMonergyPlatform("Audit Service LOCAL composition");
builder.Services.AddHealthChecks();
if (!string.Equals(builder.Configuration["Monergy:D11:Profile"], profile, StringComparison.Ordinal))
    throw new InvalidOperationException("The LOCAL Audit composition host is restricted to persisted-reporting.");
PhysicalPersistenceGuard.EnsureAllowed(builder.Configuration);
builder.Services.AddAuditPhysicalPersistence(builder.Configuration);

var expectedToken = PhysicalPersistenceGuard.Require(builder.Configuration, "Monergy:D11:LocalTransportToken");
var stallOncePath = builder.Configuration["Monergy:D11:AuditStallOncePath"];
var loseReceiptOncePath = builder.Configuration["Monergy:D11:AuditLoseReceiptOncePath"];
var app = builder.Build();
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = static _ => false });
app.MapHealthChecks("/health/ready");
app.MapPost("/contracts/cid-061/v1", async (HttpRequest request, AuditableEvent source,
    AuditApplication audit, CancellationToken cancellationToken) =>
{
    if (!ValidToken(request.Headers["X-Monergy-Local-Transport"].ToString(), expectedToken))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (!LocalAuditProtocol.IsValidReportingEvent(source))
        return Results.BadRequest(new { code = "audit.reporting-event.invalid" });
    try
    {
        if (!string.IsNullOrWhiteSpace(stallOncePath) && File.Exists(stallOncePath))
        {
            File.Delete(stallOncePath);
            await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        }
        var result = await audit.ConsumeWithDispositionAsync(source, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(loseReceiptOncePath) && File.Exists(loseReceiptOncePath))
        {
            File.Delete(loseReceiptOncePath);
            request.HttpContext.Abort();
            return Results.Empty;
        }
        return Results.Ok(LocalAuditProtocol.Receipt(source, result));
    }
    catch (ArgumentException)
    {
        return Results.BadRequest(new { code = "audit.event.invalid" });
    }
});
app.MapGet("/operations/local/audit/events/{eventId}", async (HttpRequest request, string eventId,
    AuditApplication audit, CancellationToken cancellationToken) =>
{
    if (!ValidToken(request.Headers["X-Monergy-Local-Transport"].ToString(), expectedToken))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    var record = (await audit.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        .SingleOrDefault(value => value.SourceEventId == eventId);
    return record is null
        ? Results.NotFound(new { code = "audit.event.not-found" })
        : Results.Ok(new
        {
            recipient = LocalAuditProtocol.Recipient,
            sourceEventId = record.SourceEventId,
            sourceContractId = record.SourceContractId,
            record.EventName,
            eventVersion = ContractGuard.CurrentVersion,
            record.Producer,
            record.SubjectType,
            record.SubjectId,
            record.AuditEvidenceId,
        });
});
app.MapGet("/operations/local/audit/status", async (AuditApplication audit, CancellationToken cancellationToken) =>
{
    var records = await audit.ReadAllAsync(cancellationToken).ConfigureAwait(false);
    return Results.Ok(new { state = "READY", evidenceCount = records.Count });
});
app.MapPost("/operations/local/control/stop", (HttpRequest request, IHostApplicationLifetime lifetime) =>
{
    if (!ValidToken(request.Headers["X-Monergy-Local-Control"].ToString(),
        PhysicalPersistenceGuard.Require(builder.Configuration, "Monergy:D11:ControllerToken")))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    lifetime.StopApplication();
    return Results.Accepted();
});
await app.RunAsync();

static bool ValidToken(string actual, string expected)
{
    var actualBytes = Encoding.UTF8.GetBytes(actual);
    var expectedBytes = Encoding.UTF8.GetBytes(expected);
    return actualBytes.Length == expectedBytes.Length &&
        CryptographicOperations.FixedTimeEquals(actualBytes, expectedBytes);
}

public partial class Program;
