using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DbUp;
using Monergy.Platform.ControlPlane;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
if (!builder.Environment.IsDevelopment() || builder.Configuration["Monergy:ExecutionZone"] != "LOCAL")
    throw new InvalidOperationException("Tenant provisioning owner is LOCAL Development-only.");
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 65_536);
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
    options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
    options.SerializerOptions.RespectRequiredConstructorParameters = true;
});
builder.Services.AddSingleton<LocalTenantProvisioningOwner>();
var app = builder.Build();
app.Use(async (http, next) =>
{
    http.Response.Headers.CacheControl = "no-store";
    try { await next(http).ConfigureAwait(false); }
    catch (TenantProvisioningOwnerException failure)
    {
        http.Response.StatusCode = failure.Status;
        await http.Response.WriteAsJsonAsync(new { error = failure.Code }, http.RequestAborted).ConfigureAwait(false);
    }
    catch (Exception failure)
    {
        if (http.Response.HasStarted) throw;
        var (status, error) = failure switch
        {
            UnauthorizedAccessException or IOException => (507, "TENANT_PROVISIONING_STORAGE_UNAVAILABLE"),
            JsonException or NotSupportedException => (422, "TENANT_PROVISIONING_STATE_INVALID"),
            NpgsqlException => (503, "TENANT_PROVISIONING_DATABASE_UNAVAILABLE"),
            HttpRequestException => (502, "TENANT_PROVISIONING_DEPENDENCY_UNAVAILABLE"),
            InvalidOperationException => (500, "TENANT_PROVISIONING_CONFIGURATION_INVALID"),
            _ => (500, "TENANT_PROVISIONING_OWNER_FAILED"),
        };
        TenantProvisioningOwnerLog.Failure(app.Logger, failure.GetType().Name, null);
        http.Response.StatusCode = status;
        await http.Response.WriteAsJsonAsync(new { error },
            http.RequestAborted).ConfigureAwait(false);
    }
});
app.MapGet("/internal/health/ready", (HttpContext http, LocalTenantProvisioningOwner owner) =>
    owner.AuthorizedProvisioning(http) ? Results.Ok(new { status = "ready" }) : Results.StatusCode(403));
app.MapPost("/internal/local/v1/tenants/{tenantId}/steps/{step}", async (
    string tenantId, TenantProvisioningStep step, TenantRegistryRecord tenant,
    HttpContext http, LocalTenantProvisioningOwner owner) =>
{
    if (!owner.AuthorizedProvisioning(http)) return Results.StatusCode(403);
    return Results.Ok(await owner.ExecuteAsync(tenantId, step, tenant, http.RequestAborted).ConfigureAwait(false));
});
app.MapGet("/internal/local/v1/tenant-bindings/{service}/{tenantId}", async (
    string service, string tenantId, HttpContext http, LocalTenantProvisioningOwner owner) =>
{
    if (!owner.AuthorizedBinding(http, service)) return Results.StatusCode(403);
    var binding = await owner.ResolveBindingAsync(service, tenantId, http.RequestAborted).ConfigureAwait(false);
    return binding is null ? Results.NotFound() : Results.Ok(binding);
});
await app.RunAsync();

file sealed class LocalTenantProvisioningOwner : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private static readonly string[] Services =
        ["access-management", "customer-identity", "audit", "consent", "application-case"];
    private readonly IConfiguration configuration;
    private readonly string provisioningToken;
    private readonly IReadOnlyDictionary<string, string> bindingTokens;
    private readonly string clusterConnection;
    private readonly string statePath;
    private readonly FileProtectedIntegrationSecretStore secrets;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly HttpClient client = new(new SocketsHttpHandler { AllowAutoRedirect = false })
    { Timeout = TimeSpan.FromSeconds(15) };

    public LocalTenantProvisioningOwner(IConfiguration configuration)
    {
        this.configuration = configuration;
        var root = configuration.GetRequiredSection("Monergy:TenantProvisioning");
        provisioningToken = Required(root, "Token");
        clusterConnection = Required(root, "ClusterConnection");
        statePath = Required(root, "StatePath");
        secrets = new(Required(root, "BindingSecretsPath"), Required(root, "BindingProtectionKey"));
        bindingTokens = Services.ToDictionary(service => service,
            service => Required(root, "BindingTokens:" + service), StringComparer.Ordinal);
        if (bindingTokens.Values.Append(provisioningToken).Distinct(StringComparer.Ordinal).Count() != Services.Length + 1)
            throw new InvalidOperationException("Tenant provisioning workload credentials must be separate.");
        var cluster = new NpgsqlConnectionStringBuilder(clusterConnection);
        if (cluster.Host != "postgres" || cluster.Username != "postgres" || string.IsNullOrWhiteSpace(cluster.Password))
            throw new InvalidOperationException("Tenant provisioning cluster binding is invalid.");
    }

    public bool AuthorizedProvisioning(HttpContext http) => Matches(
        http.Request.Headers["X-Monergy-Tenant-Provisioning"].ToString(), provisioningToken);

    public bool AuthorizedBinding(HttpContext http, string service) =>
        bindingTokens.TryGetValue(service, out var expected) && Matches(
            http.Request.Headers["X-Monergy-Tenant-Binding"].ToString(), expected);

    public async Task<TenantProvisioningResult> ExecuteAsync(string tenantId, TenantProvisioningStep step,
        TenantRegistryRecord tenant, CancellationToken ct)
    {
        ValidateTenant(tenantId, tenant);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var state = await ReadAsync(ct).ConfigureAwait(false);
            var record = state.Tenants.GetValueOrDefault(tenantId) ?? new TenantState
            {
                TenantId = tenantId,
                InitialAdministratorEmail = tenant.InitialAdministratorEmail,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            if (record.InitialAdministratorEmail != tenant.InitialAdministratorEmail)
                return TenantProvisioningResult.Failure("TENANT_PROVISIONING_IDENTITY_CONFLICT");
            state.Tenants[tenantId] = record;
            try
            {
                switch (step)
                {
                    case TenantProvisioningStep.PlacementResolved:
                        if (tenant.PlacementMode != TenantPlacementMode.Shared)
                            return TenantProvisioningResult.Failure("DEDICATED_PLACEMENT_UNAVAILABLE");
                        break;
                    case TenantProvisioningStep.ServiceDatabasesProvisioned:
                        await ProvisionDatabasesAsync(record, ct).ConfigureAwait(false);
                        break;
                    case TenantProvisioningStep.ServiceMigrationsApplied:
                        await ApplyMigrationsAsync(record, ct).ConfigureAwait(false);
                        break;
                    case TenantProvisioningStep.InitialAdministratorIdentityProvisioned:
                        await ProvisionIdentityAsync(record, tenant.RequestId, ct).ConfigureAwait(false);
                        break;
                    case TenantProvisioningStep.AccessManagementBootstrapped:
                        await BootstrapAccessAsync(record, ct).ConfigureAwait(false);
                        break;
                    case TenantProvisioningStep.MandatoryReadinessValidated:
                        await ValidateReadinessAsync(record, ct).ConfigureAwait(false);
                        break;
                    default:
                        return TenantProvisioningResult.Failure("TENANT_PROVISIONING_STEP_INVALID");
                }
                if (!record.CompletedSteps.Contains(step)) record.CompletedSteps.Add(step);
                record.UpdatedAt = DateTimeOffset.UtcNow;
                record.Audit.Add(new("ProvisioningStepCompleted", step.ToString(), record.UpdatedAt));
                await SaveAsync(state, ct).ConfigureAwait(false);
                var evidence = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
                    tenantId + ":" + (int)step + ":" + record.UpdatedAt.ToUniversalTime().ToString("O"))));
                return TenantProvisioningResult.Success("local-provisioning-proof:" + evidence);
            }
            catch (TenantProvisioningOwnerException failure)
            {
                record.UpdatedAt = DateTimeOffset.UtcNow;
                record.Audit.Add(new("ProvisioningFailed", failure.Code, record.UpdatedAt));
                await SaveAsync(state, ct).ConfigureAwait(false);
                return TenantProvisioningResult.Failure(failure.Code);
            }
        }
        finally { gate.Release(); }
    }

    public async Task<object?> ResolveBindingAsync(string service, string tenantId, CancellationToken ct)
    {
        Identifier(tenantId, 64);
        if (!Services.Contains(service, StringComparer.Ordinal)) return null;
        // Binding resolution is deliberately lock-free. A provisioning step may
        // call a service that resolves its tenant binding back through this owner;
        // taking the provisioning gate here would deadlock that re-entrant path.
        // State is published by atomic replacement, and bindings are exposed only
        // after the preceding database-provisioning step has committed its state.
        var state = await ReadAsync(ct).ConfigureAwait(false);
        if (!state.Tenants.TryGetValue(tenantId, out var tenant) ||
            !tenant.Databases.TryGetValue(service, out var database)) return null;
        var connection = await secrets.ResolveAsync(Reference(service, tenantId, "runtime-connection"), ct).ConfigureAwait(false);
        return connection is null ? null : new { tenantId, service, connectionString = connection, version = database.Version };
    }

    private async Task ProvisionDatabasesAsync(TenantState tenant, CancellationToken ct)
    {
        await using var cluster = new NpgsqlConnection(clusterConnection);
        await cluster.OpenAsync(ct).ConfigureAwait(false);
        foreach (var service in Services)
        {
            if (!tenant.Databases.TryGetValue(service, out var database))
            {
                var suffix = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(tenant.TenantId)))[..12];
                var serviceSuffix = service switch
                {
                    "access-management" => "am", "customer-identity" => "ci", "application-case" => "case",
                    _ => service,
                };
                var name = "pa2_" + suffix + "_" + serviceSuffix.Replace("-", "", StringComparison.Ordinal);
                database = new() { Name = name, Version = 1 };
                tenant.Databases[service] = database;
                foreach (var role in RoleNames(database.Name, service))
                {
                    var password = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
                    await secrets.SetAsync(Reference(service, tenant.TenantId, role.Suffix), password,
                        "tenant-provisioning-owner", ct).ConfigureAwait(false);
                }
            }
            var roles = RoleNames(database.Name, service).ToArray();
            foreach (var role in roles)
            {
                var password = await RequiredSecretAsync(Reference(service, tenant.TenantId, role.Suffix), ct)
                    .ConfigureAwait(false);
                if (await ScalarAsync<string?>(cluster, "SELECT rolname FROM pg_roles WHERE rolname=@value;", role.Name, ct).ConfigureAwait(false) is null)
                    await ExecuteAsync(cluster, $"CREATE ROLE {Quote(role.Name)} LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION PASSWORD '{password}';", ct).ConfigureAwait(false);
                else
                    // A process may stop after PostgreSQL committed a role but before the
                    // encrypted binding state was persisted. Reapply the protected password
                    // so a safe retry cannot leave the retained role and binding out of sync.
                    await ExecuteAsync(cluster, $"ALTER ROLE {Quote(role.Name)} WITH LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION PASSWORD '{password}';", ct).ConfigureAwait(false);
            }
            var actualOwner = await ScalarAsync<string?>(cluster,
                "SELECT pg_get_userbyid(datdba) FROM pg_database WHERE datname=@value;", database.Name, ct).ConfigureAwait(false);
            if (actualOwner is null)
                await ExecuteAsync(cluster, $"CREATE DATABASE {Quote(database.Name)} OWNER {Quote(database.Name + "_owner")};", ct).ConfigureAwait(false);
            else if (actualOwner != database.Name + "_owner")
                throw new TenantProvisioningOwnerException("DATABASE_OWNER_MISMATCH");
            await ExecuteAsync(cluster, $"REVOKE ALL ON DATABASE {Quote(database.Name)} FROM PUBLIC; GRANT CONNECT ON DATABASE {Quote(database.Name)} TO {Quote(database.Name + "_runtime")};" +
                (service == "access-management" ? $"GRANT CONNECT ON DATABASE {Quote(database.Name)} TO {Quote(database.Name + "_delivery")};" : ""), ct).ConfigureAwait(false);
            var runtimePassword = await RequiredSecretAsync(Reference(service, tenant.TenantId, "runtime"), ct).ConfigureAwait(false);
            await secrets.SetAsync(Reference(service, tenant.TenantId, "runtime-connection"),
                Connection(database.Name, database.Name + "_runtime", runtimePassword), "tenant-provisioning-owner", ct).ConfigureAwait(false);
        }
    }

    private async Task ApplyMigrationsAsync(TenantState tenant, CancellationToken ct)
    {
        foreach (var (service, database) in tenant.Databases.OrderBy(item => item.Key == "access-management" ? 1 : 0))
        {
            var ownerPassword = await RequiredSecretAsync(Reference(service, tenant.TenantId, "owner"), ct).ConfigureAwait(false);
            var connection = Connection(database.Name, database.Name + "_owner", ownerPassword);
            if (service == "access-management")
            {
                // AM's schema/bootstrap is completed after the C&I owner returns the canonical actor.
                continue;
            }
            var migrationService = service switch
            {
                "customer-identity" => "customer-identity", "application-case" => "application-case", _ => service,
            };
            var result = DeployChanges.To.PostgresqlDatabase(connection)
                .JournalToPostgresqlTable("public", "monergy_migration_history")
                .WithScriptsFromFileSystem("/source/services/" + migrationService + "/migrations", new DbUp.ScriptProviders.FileSystemScriptOptions { IncludeSubDirectories = false })
                .LogToNowhere().Build().PerformUpgrade();
            if (!result.Successful) throw new TenantProvisioningOwnerException("SERVICE_MIGRATION_FAILED");
            await using var db = new NpgsqlConnection(connection);
            await db.OpenAsync(ct).ConfigureAwait(false);
            var table = service switch
            {
                "audit" => "audit.access_tenant_identity", "consent" => "consent.tenant_identity",
                "application-case" => "application_case.tenant_identity", _ => "customer_identity.tenant_identity",
            };
            var version = service == "application-case" ? 6 : 1;
            await ExecuteAsync(db, $"INSERT INTO {table} VALUES(true,@tenant,{version}) ON CONFLICT(singleton) DO NOTHING;", ct, tenant.TenantId).ConfigureAwait(false);
            await ExecuteAsync(db, Grants(service, database.Name), ct).ConfigureAwait(false);
        }
    }

    private async Task ProvisionIdentityAsync(TenantState tenant, string requestId, CancellationToken ct)
    {
        var url = Required(configuration.GetRequiredSection("Monergy:TenantProvisioning"), "CustomerIdentityUrl");
        var token = Required(configuration.GetRequiredSection("Monergy:TenantProvisioning"), "CustomerIdentityToken");
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(requestId + ":initial-admin")));
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(url), "internal/v1/tenant-identities/provision"));
        request.Headers.Add("X-Monergy-Tenant", tenant.TenantId);
        request.Headers.Add("X-Monergy-Owner-Token", token);
        request.Content = JsonContent.Create(new
        {
            tenantId = tenant.TenantId, importId = "platform-onboarding-" + tenant.TenantId,
            rowNumber = 1, normalizedEmail = tenant.InitialAdministratorEmail, idempotencyKey = key,
        });
        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new TenantProvisioningOwnerException("INITIAL_ADMINISTRATOR_IDENTITY_FAILED");
        var receipt = await response.Content.ReadFromJsonAsync<IdentityReceipt>(cancellationToken: ct).ConfigureAwait(false)
            ?? throw new TenantProvisioningOwnerException("INITIAL_ADMINISTRATOR_IDENTITY_INVALID");
        if (receipt.TenantId != tenant.TenantId || receipt.NormalizedEmail != tenant.InitialAdministratorEmail ||
            receipt.IdempotencyKey != key || string.IsNullOrWhiteSpace(receipt.ActorId))
            throw new TenantProvisioningOwnerException("INITIAL_ADMINISTRATOR_IDENTITY_INVALID");
        tenant.InitialAdministratorActorId = receipt.ActorId;
    }

    private async Task BootstrapAccessAsync(TenantState tenant, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tenant.InitialAdministratorActorId))
            throw new TenantProvisioningOwnerException("INITIAL_ADMINISTRATOR_IDENTITY_REQUIRED");
        var database = tenant.Databases["access-management"];
        var password = await RequiredSecretAsync(Reference("access-management", tenant.TenantId, "owner"), ct).ConfigureAwait(false);
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        start.ArgumentList.Add("/app/am-migrate/Monergy.AccessManagement.Migrations.dll");
        start.ArgumentList.Add(tenant.TenantId);
        start.ArgumentList.Add(tenant.InitialAdministratorActorId);
        start.ArgumentList.Add(tenant.InitialAdministratorEmail);
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        start.Environment["MONERGY_AM_REFERENCE_PROVISIONING"] = "1";
        start.Environment["MONERGY_AM_CONTROL_PLANE_PROVISIONING"] = "1";
        start.Environment["MONERGY_AM_MIGRATION_CONNECTION"] = Connection(database.Name, database.Name + "_owner", password);
        using var process = Process.Start(start) ?? throw new TenantProvisioningOwnerException("ACCESS_BOOTSTRAP_FAILED");
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        if (process.ExitCode != 0) throw new TenantProvisioningOwnerException("ACCESS_BOOTSTRAP_FAILED");
        await using var db = new NpgsqlConnection(Connection(database.Name, database.Name + "_owner", password));
        await db.OpenAsync(ct).ConfigureAwait(false);
        await ExecuteAsync(db, Grants("access-management", database.Name), ct).ConfigureAwait(false);
    }

    private async Task ValidateReadinessAsync(TenantState tenant, CancellationToken ct)
    {
        foreach (var (service, database) in tenant.Databases)
        {
            var connectionString = await RequiredSecretAsync(Reference(service, tenant.TenantId, "runtime-connection"), ct).ConfigureAwait(false);
            await using var db = new NpgsqlConnection(connectionString);
            await db.OpenAsync(ct).ConfigureAwait(false);
            var secure = await ScalarAsync<bool>(db, """
                SELECT NOT has_database_privilege(current_user,current_database(),'CREATE')
                   AND NOT has_schema_privilege(current_user,'public','CREATE')
                   AND NOT EXISTS(SELECT 1 FROM pg_roles WHERE rolname=current_user AND (rolsuper OR rolcreatedb OR rolcreaterole OR rolreplication));
                """, null, ct).ConfigureAwait(false);
            if (!secure) throw new TenantProvisioningOwnerException("RUNTIME_PRIVILEGE_INVALID");
            var valid = service switch
            {
                "access-management" => await ScalarAsync<bool>(db, """
                    SELECT EXISTS(SELECT 1 FROM am.tenant_identity WHERE tenant_id=@tenant AND schema_version=8
                        AND initial_admin_actor_id=@actor AND default_access_pack_version='2.12')
                      AND EXISTS(SELECT 1 FROM am.access_subjects WHERE actor_id=@actor AND status='Active'
                        AND administrative_authority='TenantAdmin' AND business_role_id IS NULL);
                    """, tenant.TenantId, ct, tenant.InitialAdministratorActorId).ConfigureAwait(false),
                "customer-identity" => await ScalarAsync<bool>(db,
                    "SELECT EXISTS(SELECT 1 FROM customer_identity.tenant_principals WHERE actor_id=@actor AND normalized_email=@email);",
                    tenant.InitialAdministratorActorId, ct, tenant.InitialAdministratorEmail).ConfigureAwait(false),
                _ => true,
            };
            if (!valid) throw new TenantProvisioningOwnerException("OWNER_STATE_NOT_READY");
        }
    }

    private async Task<string> RequiredSecretAsync(string reference, CancellationToken ct) =>
        await secrets.ResolveAsync(reference, ct).ConfigureAwait(false) ??
        throw new TenantProvisioningOwnerException("TENANT_BINDING_SECRET_UNAVAILABLE");

    private async Task<State> ReadAsync(CancellationToken ct)
    {
        if (!File.Exists(statePath)) return new();
        await using var stream = new FileStream(statePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        return await JsonSerializer.DeserializeAsync<State>(stream, Json, ct).ConfigureAwait(false) ??
            throw new TenantProvisioningOwnerException("TENANT_PROVISIONING_STATE_INVALID");
    }

    private async Task SaveAsync(State state, CancellationToken ct)
    {
        var folder = Path.GetDirectoryName(statePath);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
        var temporary = statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, state, Json, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }
            File.Move(temporary, statePath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static IEnumerable<(string Suffix, string Name)> RoleNames(string database, string service)
    {
        yield return ("owner", database + "_owner");
        yield return ("runtime", database + "_runtime");
        if (service == "access-management") yield return ("delivery", database + "_delivery");
    }

    private static string Grants(string service, string database)
    {
        var runtime = Quote(database + "_runtime");
        var delivery = Quote(database + "_delivery");
        var schema = service switch { "access-management" => "am", "customer-identity" => "customer_identity", "application-case" => "application_case", _ => service };
        var common = $"REVOKE CREATE ON SCHEMA public FROM PUBLIC; GRANT USAGE ON SCHEMA {schema} TO {runtime}; GRANT SELECT ON ALL TABLES IN SCHEMA {schema} TO {runtime};";
        return common + (service switch
        {
            "access-management" => $"GRANT INSERT,UPDATE,DELETE ON am.permissions,am.roles,am.permission_capabilities,am.role_permissions,am.access_subjects,am.resource_grants,am.customer_relationships,am.groups,am.group_members,am.import_batches,am.import_rows,am.import_email_reservations TO {runtime}; GRANT INSERT,UPDATE ON am.access_requests TO {runtime}; GRANT INSERT,DELETE ON am.subject_permissions TO {runtime}; GRANT INSERT ON am.access_request_events,am.access_request_permission_grants TO {runtime}; GRANT UPDATE(policy_version) ON am.policy_state TO {runtime}; GRANT INSERT ON am.outbox TO {runtime}; GRANT USAGE ON SCHEMA am TO {delivery}; GRANT SELECT ON am.tenant_identity,am.policy_state,am.outbox,am.outbox_delivery,am.outbox_redrives TO {delivery}; GRANT INSERT,UPDATE ON am.outbox_delivery TO {delivery}; GRANT INSERT ON am.outbox_redrives TO {delivery};",
            "customer-identity" => $"GRANT INSERT(session_hash,actor_id,subject_version,device_class,client_family),UPDATE(revoked) ON customer_identity.trusted_sessions TO {runtime}; GRANT INSERT,UPDATE(version) ON customer_identity.access_subject_versions TO {runtime}; GRANT UPDATE(version) ON customer_identity.access_policy_version TO {runtime}; GRANT INSERT ON customer_identity.access_event_inbox TO {runtime}; GRANT INSERT(actor_id,normalized_email) ON customer_identity.tenant_principals TO {runtime}; GRANT INSERT ON customer_identity.provisioning_receipts TO {runtime}; GRANT INSERT,UPDATE ON customer_identity.email_access_challenges,customer_identity.registration_challenges,customer_identity.registered_credentials,customer_identity.persona_credentials TO {runtime}; GRANT INSERT ON customer_identity.email_access_events,customer_identity.registration_events,customer_identity.persona_invitation_events TO {runtime}; GRANT INSERT,UPDATE ON customer_identity.customers,customer_identity.customer_availability,customer_identity.customer_pan,customer_identity.customer_profiles TO {runtime}; GRANT INSERT ON customer_identity.customer_status_changes,customer_identity.customer_pan_receipts,customer_identity.customer_pan_audit,customer_identity.customer_profile_receipts,customer_identity.self_session_receipts,customer_identity.account_agreement_publications,customer_identity.account_agreement_acceptances,customer_identity.kyc_events TO {runtime};",
            "audit" => $"GRANT INSERT ON audit.evidence,audit.inbox,audit.access_event_inbox,audit.product_activity TO {runtime};",
            "consent" => $"GRANT INSERT ON consent.grants,consent.receipts,consent.audit,consent.outbox,consent.provider_grants,consent.provider_evidence TO {runtime}; GRANT UPDATE(version,folder_id,revoked_at) ON consent.provider_grants TO {runtime}; GRANT UPDATE(revoked_at) ON consent.grants TO {runtime}; GRANT UPDATE(version) ON consent.state TO {runtime};",
            "application-case" => $"GRANT INSERT ON application_case.assistance_cases,application_case.assistance_case_operations,application_case.assistance_case_events,application_case.advisor_engagements,application_case.advisor_engagement_activities,application_case.advisor_engagement_operations,application_case.lender_application_events,application_case.lender_application_operations,application_case.financial_review_notes,application_case.financial_review_certification_sources,application_case.financial_review_events,application_case.financial_review_operations,application_case.property_valuation_revisions,application_case.property_valuation_revision_evidence,application_case.property_valuation_events,application_case.property_valuation_operations,application_case.legal_review_revisions,application_case.legal_review_revision_evidence,application_case.legal_review_events,application_case.legal_review_operations TO {runtime}; GRANT UPDATE(state,version,accepted_at,accepted_by) ON application_case.assistance_cases TO {runtime}; GRANT UPDATE(version,application_stage,updated_at) ON application_case.advisor_engagements TO {runtime}; GRANT UPDATE(state,version,decision,decision_reason,decided_at,decided_by) ON application_case.lender_applications TO {runtime}; GRANT UPDATE(reviewed_at,reviewed_by) ON application_case.lender_application_documents TO {runtime}; GRANT UPDATE(state,version,certification_outcome,certification_rationale,certified_at,certified_by,certified_policy_version,certified_subject_version,certified_customer_version,certified_consent_version) ON application_case.financial_professional_reviews TO {runtime}; GRANT UPDATE(reviewed_at,reviewed_by) ON application_case.financial_review_sources TO {runtime}; GRANT UPDATE(state,version) ON application_case.property_valuation_assignments,application_case.legal_review_assignments TO {runtime}; GRANT UPDATE(inspected_at,inspected_by) ON application_case.property_valuation_evidence,application_case.legal_review_evidence TO {runtime};",
            _ => throw new TenantProvisioningOwnerException("TENANT_SERVICE_INVALID"),
        });
    }

    private static string Connection(string database, string user, string password) => new NpgsqlConnectionStringBuilder
    { Host = "postgres", Database = database, Username = user, Password = password, Timeout = 10, IncludeErrorDetail = false }.ConnectionString;
    private static string Quote(string value) => '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
    private static string Reference(string service, string tenant, string kind) => "tenant-binding:" + service + ":" + tenant + ":" + kind;
    private static string Required(IConfiguration section, string name) => !string.IsNullOrWhiteSpace(section[name])
        ? section[name]! : throw new InvalidOperationException("Tenant provisioning configuration is incomplete.");
    private static bool Matches(string supplied, string expected) => supplied.Length == expected.Length &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(expected));
    private static void ValidateTenant(string tenantId, TenantRegistryRecord tenant)
    {
        Identifier(tenantId, 64);
        if (tenant.TenantId != tenantId || tenant.Environment != "LOCAL" || tenant.PlacementMode != TenantPlacementMode.Shared ||
            !string.Equals(tenant.InitialAdministratorEmail, tenant.InitialAdministratorEmail.Trim(), StringComparison.Ordinal) ||
            tenant.InitialAdministratorEmail.Any(char.IsUpper))
            throw new TenantProvisioningOwnerException("TENANT_PROVISIONING_REQUEST_INVALID", 400);
    }
    private static void Identifier(string? value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || !char.IsAsciiLetterOrDigit(value[0]) ||
            value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':')))
            throw new TenantProvisioningOwnerException("TENANT_IDENTIFIER_INVALID", 400);
    }
    private static async Task ExecuteAsync(NpgsqlConnection db, string sql, CancellationToken ct, string? tenant = null)
    {
        await using var command = new NpgsqlCommand(sql, db);
        if (tenant is not null) command.Parameters.AddWithValue("tenant", tenant);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
    private static async Task<T> ScalarAsync<T>(NpgsqlConnection db, string sql, string? value,
        CancellationToken ct, string? second = null)
    {
        await using var command = new NpgsqlCommand(sql, db);
        if (value is not null) command.Parameters.AddWithValue(sql.Contains("@tenant", StringComparison.Ordinal) ? "tenant" :
            sql.Contains("@actor", StringComparison.Ordinal) ? "actor" : "value", value);
        if (second is not null) command.Parameters.AddWithValue(sql.Contains("@actor", StringComparison.Ordinal) &&
            command.Parameters.Contains("tenant") ? "actor" : sql.Contains("@email", StringComparison.Ordinal) ? "email" : "second", second);
        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is null or DBNull ? default! : (T)result;
    }
    public void Dispose() { client.Dispose(); secrets.Dispose(); gate.Dispose(); }

    private sealed class State { public Dictionary<string, TenantState> Tenants { get; init; } = new(StringComparer.Ordinal); }
    private sealed class TenantState
    {
        public string TenantId { get; init; } = "";
        public string InitialAdministratorEmail { get; init; } = "";
        public string? InitialAdministratorActorId { get; set; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset UpdatedAt { get; set; }
        public Dictionary<string, DatabaseState> Databases { get; init; } = new(StringComparer.Ordinal);
        public List<TenantProvisioningStep> CompletedSteps { get; init; } = [];
        public List<AuditEntry> Audit { get; init; } = [];
    }
    private sealed class DatabaseState { public string Name { get; init; } = ""; public long Version { get; init; } }
    private sealed record AuditEntry(string Action, string Result, DateTimeOffset Timestamp);
    private sealed record IdentityReceipt(string TenantId, string NormalizedEmail, string ActorId, string IdempotencyKey);
}

file sealed class TenantProvisioningOwnerException(string code, int status = 503) : Exception(code)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}

file static class TenantProvisioningOwnerLog
{
    public static readonly Action<ILogger, string, Exception?> Failure = LoggerMessage.Define<string>(
        LogLevel.Error, new EventId(6201, nameof(Failure)),
        "Tenant provisioning failed with sanitized category {FailureCategory}.");
}
