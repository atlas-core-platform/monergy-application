using System.Text.Json.Serialization;
using Monergy.AccessManagement;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 262_144);
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
    options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
    options.SerializerOptions.RespectRequiredConstructorParameters = true;
});
builder.Services.AddSingleton(new CapabilityCatalog());
builder.Services.AddSingleton<AccessRequestTargetCatalog>();
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddMemoryCache(options => options.SizeLimit = 1_000);
builder.Services.AddSingleton<AuthorizationEvaluator>();

var useReference =
    builder.Environment.IsDevelopment() &&
    string.Equals(builder.Configuration["Monergy:AccessManagement:Adapter"], "reference", StringComparison.Ordinal);
var usePostgresReference =
    builder.Environment.IsDevelopment() &&
    string.Equals(builder.Configuration["Monergy:AccessManagement:Adapter"], "postgres-reference", StringComparison.Ordinal);

if (usePostgresReference)
{
    builder.Services.AddSingleton<ITenantConnectionFactory>(_ => new ReferenceTenantConnectionFactory(
        builder.Configuration.GetSection("Monergy:AccessManagement:TenantDatabases").GetChildren()
            .ToDictionary(entry => entry.Key, entry => entry.Value ?? "", StringComparer.Ordinal)));
    builder.Services.AddSingleton<PostgresAccessPolicyStore>();
    builder.Services.AddSingleton<IAccessPolicyStore>(provider => provider.GetRequiredService<PostgresAccessPolicyStore>());
    builder.Services.AddSingleton<IVerifiedTenantIdentityDirectory, ReferenceTenantIdentityDirectory>();
    builder.Services.AddSingleton<PostgresAccessAdministration>();
    var ownerContext = builder.Configuration["Monergy:AccessManagement:TrustedContext"];
    if (ownerContext == "customer-identity-reference")
    {
        var route = new ReferenceDeliveryRoute(new Uri(builder.Configuration["Monergy:AccessManagement:CustomerIdentityUrl"] ?? ""),
            builder.Configuration["Monergy:AccessManagement:CustomerIdentityToken"] ?? "");
        ReferenceOwnerBoundary.Validate(route);
        builder.Services.AddSingleton<ITrustedSecurityContextResolver>(provider => new CustomerIdentityContextResolver(
            ReferenceHttpEventTransport.CreateClient(), route, provider.GetRequiredService<TimeProvider>()));
        var identityRoute = new ReferenceDeliveryRoute(route.Endpoint,
            builder.Configuration["Monergy:AccessManagement:IdentityProvisioningToken"] ?? "");
        ReferenceOwnerBoundary.Validate(identityRoute);
        if (identityRoute.Token == route.Token)
            throw new InvalidOperationException("Identity provisioning requires a separate workload credential.");
        builder.Services.AddSingleton<ITenantIdentityProvisioner>(_ => new HttpTenantIdentityProvisioner(
            ReferenceHttpEventTransport.CreateClient(), identityRoute));
    }
    else if (string.IsNullOrEmpty(ownerContext))
    {
        builder.Services.AddSingleton<ITrustedSecurityContextResolver, ReferenceTrustedSecurityContextResolver>();
        builder.Services.AddSingleton<ITenantIdentityProvisioner, ReferenceTenantIdentityProvisioner>();
    }
    else throw new InvalidOperationException("Unknown trusted-context adapter.");
}
else if (useReference)
{
    builder.Services.AddSingleton<IAccessPolicyStore, ReferenceAccessPolicyStore>();
    builder.Services.AddSingleton<ITrustedSecurityContextResolver, ReferenceTrustedSecurityContextResolver>();
}
else
{
    builder.Services.AddSingleton<IAccessPolicyStore, UnavailableAccessPolicyStore>();
    builder.Services.AddSingleton<ITrustedSecurityContextResolver, RejectingTrustedSecurityContextResolver>();
}

var customerRelationships = builder.Configuration.GetValue<bool>("Monergy:AccessManagement:CustomerRelationships");
if (customerRelationships)
{
    if (!usePostgresReference || builder.Configuration["Monergy:AccessManagement:TrustedContext"] != "customer-identity-reference" ||
        builder.Configuration["Monergy:ExecutionZone"] is not ("LOCAL" or "CI_EPHEMERAL"))
        throw new InvalidOperationException("Customer relationships require the LOCAL/CI trusted owner composition.");
    var customerRoute = new ReferenceDeliveryRoute(new Uri(builder.Configuration["Monergy:AccessManagement:CustomerIdentityUrl"] ?? ""),
        builder.Configuration["Monergy:AccessManagement:CustomerContextToken"] ?? "");
    var consentRoute = new ReferenceDeliveryRoute(new Uri(builder.Configuration["Monergy:AccessManagement:ConsentUrl"] ?? ""),
        builder.Configuration["Monergy:AccessManagement:ConsentToken"] ?? "");
    ReferenceOwnerBoundary.Validate(customerRoute);
    ReferenceOwnerBoundary.Validate(consentRoute);
    var tokens = new[] { customerRoute.Token, consentRoute.Token,
        builder.Configuration["Monergy:AccessManagement:CustomerIdentityToken"],
        builder.Configuration["Monergy:AccessManagement:IdentityProvisioningToken"],
        builder.Configuration["Monergy:AccessManagement:MembershipToken"] };
    if (tokens.Any(string.IsNullOrEmpty) || tokens.Distinct(StringComparer.Ordinal).Count() != tokens.Length)
        throw new InvalidOperationException("Owner ports require separate workload credentials.");
    builder.Services.AddSingleton<ICustomerRelationshipStore>(provider => provider.GetRequiredService<PostgresAccessPolicyStore>());
    builder.Services.AddSingleton<ICustomerContextClient>(_ => new HttpCustomerContextClient(ReferenceHttpEventTransport.CreateClient(), customerRoute));
    builder.Services.AddSingleton<ICustomerConsentClient>(_ => new HttpCustomerConsentClient(ReferenceHttpEventTransport.CreateClient(), consentRoute));
    builder.Services.AddSingleton<CustomerAccessEvaluator>();
}

var app = builder.Build();
if(customerRelationships && builder.Configuration["Monergy:AccessManagement:CustomerEnrollmentToken"] is { } enrollmentToken)
    app.MapCustomerEnrollment(enrollmentToken);
if (customerRelationships)
{
    app.MapCustomerRelationships();
    app.MapPost("/v1/authorization/customer-evaluations", async (CustomerAccessRequest request, HttpContext http,
        ITrustedSecurityContextResolver resolver, CustomerAccessEvaluator evaluator, CancellationToken ct) =>
    {
        try { AccessInput.Identifier(request.CapabilityId, 160); AccessInput.Identifier(request.CustomerId, 128); }
        catch (AccessAdministrationException) { return (IResult)Results.BadRequest(new { error = "INVALID_CUSTOMER_ACCESS_REQUEST" }); }
        var trusted = await resolver.ResolveAsync(http).ConfigureAwait(false);
        if (trusted.Context is null) return (IResult)Results.Json(new { error = trusted.Failure }, statusCode: trusted.Available ? 401 : 503);
        var decision = await evaluator.EvaluateAsync(trusted.Context, request, ct).ConfigureAwait(false);
        return (IResult)Results.Json(decision, statusCode: decision.ReasonCode == "DependencyUnavailable" ? 503 : 200);
    });
}
app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", async (IAccessPolicyStore store, ITrustedSecurityContextResolver context, HttpContext http) =>
    store.IsReady && context.IsReady &&
    (http.RequestServices.GetService<ITenantConnectionFactory>() is not { } connections ||
        await connections.CheckReadyAsync(http.RequestAborted).ConfigureAwait(false))
        ? (IResult)Results.Ok(new { status = "ready" })
        : Results.Json(new { status = "not-ready" }, statusCode: StatusCodes.Status503ServiceUnavailable));
app.MapAdvisorWorkspace();
if (customerRelationships) app.MapAdvisorCustomers();
app.MapAccessAdministration();
if (usePostgresReference && builder.Configuration["Monergy:AccessManagement:MembershipToken"] is { } membershipToken)
    app.MapMembershipPort(membershipToken);
app.MapGet("/v1/capabilities", (CapabilityCatalog catalog) =>
    Results.Ok(new { version = CapabilityCatalog.CurrentVersion, capabilities = catalog.All }));

app.MapPost("/v1/authorization/evaluations", async (
    AuthorizationEvaluationRequest request,
    HttpContext http,
    ITrustedSecurityContextResolver contextResolver,
    AuthorizationEvaluator evaluator,
    CancellationToken ct) =>
{
    if (!Valid(request))
        return (IResult)Results.BadRequest(new { error = "INVALID_AUTHORIZATION_REQUEST" });

    var trusted = await contextResolver.ResolveAsync(http).ConfigureAwait(false);
    if (trusted.Context is null)
        return (IResult)Results.Json(new { error = trusted.Failure }, statusCode:
            trusted.Available ? StatusCodes.Status401Unauthorized : StatusCodes.Status503ServiceUnavailable);

    var decision = await evaluator.EvaluateAsync(trusted.Context, request, ct).ConfigureAwait(false);
    var status = decision.ReasonCode == AuthorizationReasonCode.DependencyUnavailable
        ? StatusCodes.Status503ServiceUnavailable
        : StatusCodes.Status200OK;
    return (IResult)Results.Json(decision, statusCode: status);
});

await app.RunAsync();

static bool Valid(AuthorizationEvaluationRequest request)
{
    if (string.IsNullOrWhiteSpace(request.CapabilityId) || request.CapabilityId.Length > 160 ||
        request.CapabilityId[0] is < 'a' or > 'z' ||
        request.CapabilityId.Any(character => !(character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-')))
        return false;
    if (request.ResourceType is { } type &&
        (type.Length is < 1 or > 80 || type[0] is < 'a' or > 'z' ||
         type.Any(character => !(character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-'))))
        return false;
    if (request.ResourceId is { } id &&
        (id.Length is < 1 or > 200 || !char.IsAsciiLetterOrDigit(id[0]) ||
         id.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '-'))))
        return false;
    return true;
}
