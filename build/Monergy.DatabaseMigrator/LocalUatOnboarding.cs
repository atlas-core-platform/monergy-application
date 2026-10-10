using System.Security.Cryptography;
using System.Text.Json;
using Monergy.Platform.ControlPlane;
using Npgsql;

namespace Monergy.DatabaseMigrator;

internal static partial class LocalUatBootstrap
{
    private const string OnboardingFolder = "/var/lib/monergy-onboarding";
    private static readonly string[] ReferenceActors = ["A100", "A200", "A300", "A900", "A901"];

    private static async Task PrepareOnboardingAsync(UatProfile profile)
    {
        var bindings = new Dictionary<string, string>(StringComparer.Ordinal)
        { [profile.InstanceId + ":T001"] = "T001", [profile.InstanceId + ":T002"] = "T002" };
        using var registry = new FileTenantRegistry(Path.Combine(OnboardingFolder, "registry.json"), bindings);
        foreach (var tenant in new[] { "T001", "T002" })
        {
            var record = await registry.CreateOrGetAsync(new(profile.InstanceId + ":" + tenant, "Monergy local workspace " + tenant,
                "Local workspace " + tenant, "US", "UTC", "LOCAL", "a900@example.test", TenantPlacementMode.Shared), TimeProvider.System, default).ConfigureAwait(false);
            if (record.State is TenantLifecycleState.Requested or TenantLifecycleState.ProvisioningFailed)
                await registry.ReplaceAsync(record with { State = TenantLifecycleState.Provisioning, LastFailureCode = null, UpdatedAt = DateTimeOffset.UtcNow },
                    record.ConfigurationVersion, default).ConfigureAwait(false);
        }
    }

    private static async Task RecordOnboardingFailureAsync(UatProfile profile)
    {
        try
        {
            using var registry = new FileTenantRegistry(Path.Combine(OnboardingFolder, "registry.json"));
            foreach (var tenant in profile.Databases.Select(database => database.Tenant).Distinct(StringComparer.Ordinal))
            {
                var record = await registry.GetAsync(tenant, default).ConfigureAwait(false);
                if (record?.State == TenantLifecycleState.Provisioning)
                    await registry.ReplaceAsync(record with { State = TenantLifecycleState.ProvisioningFailed, LastFailureCode = "LOCAL_OWNER_BOOTSTRAP_FAILED", UpdatedAt = DateTimeOffset.UtcNow },
                        record.ConfigurationVersion, default).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException or JsonException)
        { Console.Error.WriteLine("The local registry could not record the failure; provisioning remains incomplete."); }
    }

    private static async Task SeedCustomerOwnershipAsync(NpgsqlConnection connection, string tenant, string instance)
    {
        await using var transaction = await connection.BeginTransactionAsync().ConfigureAwait(false);
        foreach (var actor in ReferenceActors)
        {
            await using var command = new NpgsqlCommand("""
                INSERT INTO customer_identity.tenant_principals(actor_id,normalized_email) VALUES(@actor,@email) ON CONFLICT(actor_id) DO NOTHING;
                SELECT normalized_email=@email FROM customer_identity.tenant_principals WHERE actor_id=@actor;
                """, connection, transaction);
            command.Parameters.AddWithValue("actor", actor);
            command.Parameters.AddWithValue("email", actor.ToLowerInvariant() + "@example.test");
            if (await command.ExecuteScalarAsync().ConfigureAwait(false) is not true) throw new InvalidOperationException("Reference principal mismatch.");
        }
        var primary = tenant == "T001" ? "reference-customer" : "other-customer";
        var secondary = tenant == "T001" ? "C001" : "C002";
        foreach (var customer in new[] {
            (Id: primary, Label: tenant == "T001" ? "Reference Customer" : "Other Customer", Actor: "A100", Detail: "Primary UAT customer"),
            (Id: secondary, Label: tenant == "T001" ? "Sample Customer One" : "Sample Customer Two", Actor: "A200", Detail: "Additional UAT customer") })
        {
            // Explicit owner bootstrap fixture backed by C&I principals. The
            // display-directory configuration is neither consulted nor trusted.
            await using var command = new NpgsqlCommand("""
                INSERT INTO customer_identity.customers(customer_id,display_name,secondary_label,owner_actor_id,ownership_evidence)
                VALUES(@id,@label,@detail,@actor,@evidence) ON CONFLICT(customer_id) DO NOTHING;
                SELECT owner_actor_id=@actor AND ownership_evidence=@evidence FROM customer_identity.customers WHERE customer_id=@id;
                """, connection, transaction);
            command.Parameters.AddWithValue("id", customer.Id); command.Parameters.AddWithValue("label", customer.Label);
            command.Parameters.AddWithValue("detail", customer.Detail); command.Parameters.AddWithValue("actor", customer.Actor);
            command.Parameters.AddWithValue("evidence", "local-reference-owner:" + instance + ":" + customer.Id);
            if (await command.ExecuteScalarAsync().ConfigureAwait(false) is not true) throw new InvalidOperationException("Reference customer ownership mismatch.");
        }
        await transaction.CommitAsync().ConfigureAwait(false);
    }

    private static async Task RecordOnboardingAsync(UatProfile profile, NpgsqlConnection cluster)
    {
        Directory.CreateDirectory(OnboardingFolder);
        var bindings = new Dictionary<string, string>(StringComparer.Ordinal)
        { [profile.InstanceId + ":T001"] = "T001", [profile.InstanceId + ":T002"] = "T002" };
        using var registry = new FileTenantRegistry(Path.Combine(OnboardingFolder, "registry.json"), bindings);
        var operations = Enum.GetValues<TenantProvisioningStep>().Select(step => new LocalOwnerVerification(step, profile, cluster));
        var orchestrator = new TenantOnboardingOrchestrator(registry, operations, TimeProvider.System);
        foreach (var tenant in new[] { "T001", "T002" })
        {
            await orchestrator.RequestAsync(new(profile.InstanceId + ":" + tenant, "Monergy local workspace " + tenant,
                "Local workspace " + tenant, "US", "UTC", "LOCAL", "a900@example.test", TenantPlacementMode.Shared)).ConfigureAwait(false);
            var record = await orchestrator.StartOrResumeAsync(tenant).ConfigureAwait(false);
            if (record.State is not (TenantLifecycleState.ReadyForAdmin or TenantLifecycleState.Active))
                throw new InvalidOperationException("Local tenant owner verification failed.");
        }
    }

    // The existing bootstrap above performs the idempotent owner operations. These
    // adapters independently read the resulting owner state before CP records a
    // receipt. They prove LOCAL reference provisioning, not cloud provisioning or
    // readiness of unimplemented production business consumers.
    private sealed class LocalOwnerVerification(TenantProvisioningStep step, UatProfile profile, NpgsqlConnection cluster) : ITenantProvisioningOperation
    {
        public TenantProvisioningStep ProvisioningStep => step;
        public async Task<TenantProvisioningResult> ExecuteAsync(TenantRegistryRecord tenant, string operationId, CancellationToken cancellationToken)
        {
            if (tenant.Environment != "LOCAL" || tenant.RequestId != profile.InstanceId + ":" + tenant.TenantId)
                return TenantProvisioningResult.Failure("LOCAL_PLACEMENT_MISMATCH");
            var databases = profile.Databases.Where(database => database.Tenant == tenant.TenantId).ToArray();
            if (databases.Length != 4) return TenantProvisioningResult.Failure("MANDATORY_OWNERS_INCOMPLETE");
            var facts = new List<string>();
            foreach (var database in databases)
            {
                if (step == TenantProvisioningStep.ServiceDatabasesProvisioned)
                {
                    var actual = await ScalarAsync(cluster, "SELECT pg_get_userbyid(datdba) FROM pg_database WHERE datname=@value;", database.Name).ConfigureAwait(false);
                    if ((string?)actual != database.Name + "_owner") return TenantProvisioningResult.Failure("DATABASE_OWNER_MISMATCH");
                }
                await using var connection = new NpgsqlConnection(Connection(database.Name, database.Name + "_runtime", database.RuntimePassword));
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                var schema = database.Service == "customer-identity" ? "customer_identity" : database.Service;
                var table = database.Service == "audit" ? "audit.access_tenant_identity" : schema + ".tenant_identity";
                var sql = $"SELECT tenant_id=@tenant FROM {table} WHERE singleton;";
                if (step == TenantProvisioningStep.ServiceMigrationsApplied)
                    sql = $"SELECT tenant_id=@tenant AND schema_version={(database.Service == "am" ? 5 : 1)} FROM {table} WHERE singleton;";
                if (step == TenantProvisioningStep.InitialAdministratorIdentityProvisioned && database.Service == "customer-identity")
                    sql = "SELECT EXISTS(SELECT 1 FROM customer_identity.tenant_principals WHERE actor_id='A900' AND normalized_email='a900@example.test');";
                if (step == TenantProvisioningStep.AccessManagementBootstrapped && database.Service == "am")
                    sql = """
                        SELECT EXISTS(SELECT 1 FROM am.tenant_identity WHERE singleton AND tenant_id=@tenant AND initial_admin_actor_id='A900' AND default_access_pack_version='1.0')
                            AND EXISTS(SELECT 1 FROM am.access_subjects WHERE actor_id='A900' AND administrative_authority='TenantAdmin' AND business_role_id IS NULL AND status='Active');
                        """;
                if (step == TenantProvisioningStep.MandatoryReadinessValidated)
                    sql = $"SELECT NOT has_schema_privilege(current_user,'{schema}','CREATE') AND NOT has_schema_privilege(current_user,'public','CREATE') AND NOT has_database_privilege(current_user,current_database(),'CREATE') AND NOT has_table_privilege(current_user,'{table}','INSERT,UPDATE,DELETE,TRUNCATE');";
                await using var command = new NpgsqlCommand(sql, connection);
                command.Parameters.AddWithValue("tenant", tenant.TenantId);
                if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
                    return TenantProvisioningResult.Failure("OWNER_STATE_NOT_VERIFIED");
                facts.Add(database.Service + ":" + step + ":verified");
            }
            var document = JsonSerializer.SerializeToUtf8Bytes(new { profile.InstanceId, tenant.TenantId, operationId, step, facts }, Json);
            var hash = Convert.ToHexStringLower(SHA256.HashData(document));
            var file = Path.Combine(OnboardingFolder, "proof-" + hash + ".json");
            if (!File.Exists(file)) await File.WriteAllBytesAsync(file, document, cancellationToken).ConfigureAwait(false);
            return TenantProvisioningResult.Success("local-owner-proof:" + hash);
        }
    }
}
