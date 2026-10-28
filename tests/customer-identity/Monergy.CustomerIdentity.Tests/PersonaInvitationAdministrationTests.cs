using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.CustomerIdentity;
using Monergy.Services.CustomerIdentity.Application;
using Monergy.Services.CustomerIdentity.Infrastructure;
using Xunit;

namespace Monergy.CustomerIdentity.Tests;

public sealed class PersonaInvitationAdministrationTests
{
    [Fact]
    public async Task CurrentAdministratorRequestsOwnerResolvedDeliveryWithoutReceivingSecrets()
    {
        await using var host = await Host.StartAsync();

        var response = await host.Client.PostAsJsonAsync("A123/send", new { });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var receipt = await response.Content.ReadFromJsonAsync<PersonaInvitationDeliveryReceipt>();
        Assert.Equal(new("A123", "Accepted"), receipt);
        var queued = Assert.Single(host.Repository.Queued);
        Assert.Equal("T001", queued.Tenant);
        Assert.Equal("person@example.test", queued.Challenge.Email);
        Assert.Equal("persona-activation", queued.Challenge.Purpose);
        Assert.Equal(64, queued.Challenge.ChallengeHash.Length);
        Assert.Equal(64, queued.Challenge.CodeHash.Length);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("person@example.test", body);
        Assert.DoesNotContain("challenge", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, host.Authority.Calls);
    }

    [Fact]
    public async Task UnknownOrAlreadyCredentialedActorGetsSameEnvelopeWithoutQueuedDelivery()
    {
        await using var host = await Host.StartAsync();

        var response = await host.Client.PostAsJsonAsync("A999/send", new { });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(new("A999", "Accepted"),
            await response.Content.ReadFromJsonAsync<PersonaInvitationDeliveryReceipt>());
        Assert.Empty(host.Repository.Queued);
    }

    [Fact]
    public async Task MissingOrChangedAdministratorAuthorityCannotQueueDelivery()
    {
        await using var denied = await Host.StartAsync();
        denied.Authority.Status = 403;
        Assert.Equal(HttpStatusCode.Forbidden,
            (await denied.Client.PostAsJsonAsync("A123/send", new { })).StatusCode);
        Assert.Empty(denied.Repository.Queued);

        await using var changed = await Host.StartAsync();
        changed.Authority.ChangeAfter = 1;
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await changed.Client.PostAsJsonAsync("A123/send", new { })).StatusCode);
        Assert.Empty(changed.Repository.Queued);
    }

    [Fact]
    public async Task DeliveryRouteIsAbsentWhenAuthenticatedEmailDeliveryIsDisabled()
    {
        await using var host = await Host.StartAsync(emailDeliveryEnabled: false);
        Assert.Equal(HttpStatusCode.NotFound,
            (await host.Client.PostAsJsonAsync("A123/send", new { })).StatusCode);
        Assert.Empty(host.Repository.Queued);
    }

    private sealed class DeliveryRepository : IPersonaInvitationDeliveryRepository
    {
        public List<(string Tenant, RegistrationCode Challenge)> Queued { get; } = [];
        public Task<string?> ReadActivationEmailAsync(string tenant, string actorId, CancellationToken ct) =>
            Task.FromResult<string?>(actorId == "A123" ? "person@example.test" : null);
        public Task QueueAsync(string tenant, RegistrationCode challenge, CancellationToken ct)
        {
            Queued.Add((tenant, challenge));
            return Task.CompletedTask;
        }
    }

    private sealed class InvitationStatus : IPersonaInvitationStatus
    {
        public Task<PersonaInvitationStatusPage> ListAsync(string tenant, string? after, CancellationToken ct) =>
            Task.FromResult(new PersonaInvitationStatusPage(tenant, [], null));
        public Task<PersonaInvitationCancelResult> CancelAsync(string tenant, string actorId, CancellationToken ct) =>
            Task.FromResult(new PersonaInvitationCancelResult(actorId, "NotCancelled"));
    }

    private sealed class Authority : HttpMessageHandler
    {
        public int Status = 200;
        public int Calls;
        public int ChangeAfter = int.MaxValue;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            var tenant = request.Headers.GetValues("X-Monergy-Tenant").Single();
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)Status)
            {
                Content = JsonContent.Create(Status == 200
                    ? (object)new TenantAdminContext(tenant, Calls > ChangeAfter ? "A901" : "A900", 1, "TenantAdmin")
                    : new { error = "ADMINISTRATOR_REQUIRED" })
            });
        }
    }

    private sealed class Host(
        WebApplication app, HttpClient client, DeliveryRepository repository, Authority authority) :
        IAsyncDisposable
    {
        public HttpClient Client => client;
        public DeliveryRepository Repository => repository;
        public Authority Authority => authority;

        public static async Task<Host> StartAsync(bool emailDeliveryEnabled = true)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = "Development"
            });
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var authority = new Authority();
            var repository = new DeliveryRepository();
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["Monergy:ExecutionZone"] = "CI_EPHEMERAL" }).Build();
            builder.Services.AddSingleton(new TenantAccessClient(configuration, new HttpClient(authority)));
            builder.Services.AddSingleton<IPersonaInvitationStatus, InvitationStatus>();
            builder.Services.AddSingleton<IPersonaInvitationDeliveryRepository>(repository);
            builder.Services.AddSingleton(new CustomerEmailCodeProtector(
                Convert.ToBase64String(new byte[32])));
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddSingleton<PersonaInvitationAdministration>();
            var app = builder.Build();
            app.MapPersonaInvitationStatus(emailDeliveryEnabled);
            await app.StartAsync();
            var url = app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()!.Addresses.Single();
            var client = new HttpClient
            {
                BaseAddress = new Uri(url + "/local/v1/administration/persona-invitations/")
            };
            client.DefaultRequestHeaders.Add("X-Monergy-Tenant", "T001");
            client.DefaultRequestHeaders.Add("X-Monergy-Session", "admin-session");
            return new(app, client, repository, authority);
        }

        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }
}
