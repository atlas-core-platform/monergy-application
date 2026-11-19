using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Xunit;

namespace Monergy.Assistant.Tests;

[Collection("Assistant physical")]
[Trait("Category", "Physical")]
public sealed class OptionalHostPhysicalTests(PhysicalFixture fixture)
{
    [Theory]
    [InlineData("missing")][InlineData("malformed-enabled")][InlineData("malformed-storage")]
    public async Task ActualAiHostAndFinancialQaAuthorizationRemainAvailableWithBrokenOptionalSettings(string fault)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.Combine(PhysicalFixture.Root, "services", "ai-intelligence") };
        start.ArgumentList.Add(Path.Combine(start.WorkingDirectory, "bin", "Release", "net10.0", "Monergy.Services.AiIntelligence.dll"));
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        start.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:" + port;
        start.Environment["OTEL_SDK_DISABLED"] = "true";
        start.Environment["Monergy__ExecutionZone"] = "CI_EPHEMERAL";
        start.Environment["Monergy__AccessIntegration__Enabled"] = "true";
        start.Environment["Monergy__FinancialQa__Enabled"] = "true";
        start.Environment["Monergy__AccessIntegration__CustomerIdentityUrl"] = "http://127.0.0.1:5197/";
        start.Environment["Monergy__AccessIntegration__AccessManagementUrl"] = "http://127.0.0.1:5088/";
        foreach (var key in new[] { "CustomerContextToken", "CustomerIdentityToken", "MembershipToken" })
            start.Environment["Monergy__AccessIntegration__" + key] = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        start.Environment["Monergy__AssistantFeedback__IngestToken"] = token;
        start.Environment["Monergy__AssistantFeedback__ReviewToken"] = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        start.Environment["Monergy__AssistantFeedback__Enabled"] = fault == "malformed-enabled" ? "definitely-not-a-boolean" : "true";
        if (fault == "malformed-storage") start.Environment["Monergy__AssistantFeedback__ConnectionString"] = "not a connection string";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Isolated AI host did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new Uri("http://127.0.0.1:" + port), Timeout = TimeSpan.FromSeconds(2) };
        try
        {
            var ready = false;
            var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
            while (DateTimeOffset.UtcNow < deadline)
            {
                Assert.False(process.HasExited);
                try { using var response = await client.GetAsync("/health/ready"); ready = response.IsSuccessStatusCode; }
                catch (HttpRequestException) { }
                catch (TaskCanceledException) { }
                if (ready) break; await Task.Delay(100);
            }
            Assert.True(ready);
            using var status = new HttpRequestMessage(HttpMethod.Get, "/internal/assistant-feedback/v1/status"); status.Headers.Add("X-Monergy-Assistant-Feedback", token);
            using var feedback = await client.SendAsync(status); Assert.Equal(HttpStatusCode.ServiceUnavailable, feedback.StatusCode);
            using var qa = await client.GetAsync("/local/v1/financial-qa/configuration"); Assert.Equal(HttpStatusCode.Unauthorized, qa.StatusCode);
            using var healthy = await client.GetAsync("/health/ready"); Assert.Equal(HttpStatusCode.OK, healthy.StatusCode); Assert.False(process.HasExited);
            Assert.NotEmpty(await fixture.ContextAsync("list"));
        }
        finally
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync();
        }
        var logs = await stdout + await stderr;
        Assert.DoesNotContain(token, logs, StringComparison.Ordinal);
        Assert.DoesNotContain("Password=", logs, StringComparison.OrdinalIgnoreCase);
    }
}
