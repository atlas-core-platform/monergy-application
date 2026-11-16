using System.Security.Cryptography;
using System.Text.Json;
using Monergy.Platform.ControlPlane;
using Npgsql;

namespace Monergy.DatabaseMigrator;

internal static partial class LocalUatBootstrap
{
    private const string OnboardingFolder = "/var/lib/monergy-onboarding";
    private const int ExpectedAccessManagementSchemaVersion = 8;
    private static readonly string[] ReferenceActors = ["A100", "A200", "A300", "A400", "A500", "A600", "A700", "A900", "A901"];

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

    private static async Task SeedLenderApplicationAsync(NpgsqlConnection connection, string tenant, string instance)
    {
        var applicationId = tenant == "T001" ? "local-loan-001" : "local-loan-002";
        var reference = tenant == "T001" ? "Lending application 001" : "Lending application 002";
        await using var command = new NpgsqlCommand("""
            INSERT INTO application_case.lender_applications(
                tenant_id,application_id,application_reference,assigned_lender_actor_id,
                assignment_source,state,version,assigned_at)
            VALUES(@tenant,@application,@reference,'A500',@source,'Assigned',1,clock_timestamp())
            ON CONFLICT(tenant_id,application_id) DO NOTHING;
            INSERT INTO application_case.lender_application_documents(
                tenant_id,application_id,document_code,label,sequence,present)
            VALUES(@tenant,@application,'identity','Identity document',1,true),
                  (@tenant,@application,'income','Income evidence',2,true),
                  (@tenant,@application,'bank-statements','Bank statements',3,true)
            ON CONFLICT(tenant_id,application_id,document_code) DO NOTHING;
            SELECT assigned_lender_actor_id='A500' AND assignment_source=@source
            FROM application_case.lender_applications
            WHERE tenant_id=@tenant AND application_id=@application;
            """, connection);
        command.Parameters.AddWithValue("tenant", tenant);
        command.Parameters.AddWithValue("application", applicationId);
        command.Parameters.AddWithValue("reference", reference);
        command.Parameters.AddWithValue("source", "local-uat-fixture:" + instance + ":" + applicationId);
        if (await command.ExecuteScalarAsync().ConfigureAwait(false) is not true)
            throw new InvalidOperationException("Reference lender assignment mismatch.");
    }

    private static async Task SeedFinancialProfessionalReviewAsync(NpgsqlConnection connection, string tenant, string instance)
    {
        var customer = tenant == "T001" ? "reference-customer" : "other-customer";
        var review = tenant == "T001" ? "local-financial-review-001" : "local-financial-review-002";
        var reference = tenant == "T001" ? "Financial review 001" : "Financial review 002";
        await using var command = new NpgsqlCommand("""
            INSERT INTO application_case.financial_professional_reviews(
                tenant_id,review_id,review_reference,customer_id,assigned_professional_actor_id,
                assignment_source,state,version,assigned_at)
            VALUES(@tenant,@review,@reference,@customer,'A600',@source,'Assigned',1,clock_timestamp())
            ON CONFLICT(tenant_id,review_id) DO NOTHING;
            INSERT INTO application_case.financial_review_sources(
                tenant_id,review_id,source_code,label,owner_service,resource_reference,
                sequence,assigned_version,current_version)
            VALUES(@tenant,@review,'profile-summary','Customer-reviewed financial profile','Financial Profile',
                    @profile,1,1,1),
                  (@tenant,@review,'supporting-evidence','Supporting evidence index','Evidence',
                    @evidence,2,1,1)
            ON CONFLICT(tenant_id,review_id,source_code) DO NOTHING;
            SELECT assigned_professional_actor_id='A600' AND customer_id=@customer AND assignment_source=@source
            FROM application_case.financial_professional_reviews
            WHERE tenant_id=@tenant AND review_id=@review;
            """, connection);
        command.Parameters.AddWithValue("tenant", tenant);
        command.Parameters.AddWithValue("review", review);
        command.Parameters.AddWithValue("reference", reference);
        command.Parameters.AddWithValue("customer", customer);
        command.Parameters.AddWithValue("source", "local-uat-fixture:" + instance + ":" + review);
        command.Parameters.AddWithValue("profile", tenant == "T001" ? "financial-profile-source-001" : "financial-profile-source-002");
        command.Parameters.AddWithValue("evidence", tenant == "T001" ? "evidence-source-001" : "evidence-source-002");
        if (await command.ExecuteScalarAsync().ConfigureAwait(false) is not true)
            throw new InvalidOperationException("Reference Financial Professional assignment mismatch.");
    }

    private static async Task SeedPropertyValuationAsync(NpgsqlConnection connection, string tenant, string instance)
    {
        var customer = tenant == "T001" ? "reference-customer" : "other-customer";
        var valuation = tenant == "T001" ? "local-property-valuation-001" : "local-property-valuation-002";
        var property = tenant == "T001" ? "property-saved" : "property-other";
        await using var command = new NpgsqlCommand("""
            INSERT INTO application_case.property_valuation_assignments(
                tenant_id,valuation_id,property_reference,property_label,customer_id,
                assigned_valuer_actor_id,assignment_source,state,version,assigned_at)
            VALUES(@tenant,@valuation,@property,'Family home',@customer,'A700',@source,'Assigned',1,clock_timestamp())
            ON CONFLICT(tenant_id,valuation_id) DO NOTHING;
            INSERT INTO application_case.property_valuation_evidence(
                tenant_id,valuation_id,evidence_code,label,owner_service,resource_reference,
                sequence,assigned_version,current_version)
            VALUES(@tenant,@valuation,'property-record','Customer-reviewed property record','Financial Profile',
                    @property,1,1,1),
                  (@tenant,@valuation,'title-evidence','Assigned title evidence','Evidence',
                    @evidence,2,1,1)
            ON CONFLICT(tenant_id,valuation_id,evidence_code) DO NOTHING;
            SELECT assigned_valuer_actor_id='A700' AND customer_id=@customer AND assignment_source=@source
            FROM application_case.property_valuation_assignments
            WHERE tenant_id=@tenant AND valuation_id=@valuation;
            """, connection);
        command.Parameters.AddWithValue("tenant", tenant);
        command.Parameters.AddWithValue("valuation", valuation);
        command.Parameters.AddWithValue("property", property);
        command.Parameters.AddWithValue("customer", customer);
        command.Parameters.AddWithValue("source", "local-uat-fixture:" + instance + ":" + valuation);
        command.Parameters.AddWithValue("evidence", tenant == "T001" ? "property-evidence-001" : "property-evidence-002");
        if (await command.ExecuteScalarAsync().ConfigureAwait(false) is not true)
            throw new InvalidOperationException("Reference Property Valuer assignment mismatch.");
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
            if (databases.Length != 5) return TenantProvisioningResult.Failure("MANDATORY_OWNERS_INCOMPLETE");
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
                var schema = database.Service switch
                {
                    "customer-identity" => "customer_identity",
                    "application-case" => "application_case",
                    _ => database.Service,
                };
                var table = database.Service == "audit" ? "audit.access_tenant_identity" : schema + ".tenant_identity";
                var sql = $"SELECT tenant_id=@tenant FROM {table} WHERE singleton;";
                if (step == TenantProvisioningStep.ServiceMigrationsApplied)
                    sql = $"SELECT tenant_id=@tenant AND schema_version={(database.Service == "am" ? ExpectedAccessManagementSchemaVersion : database.Service == "application-case" ? 5 : 1)} FROM {table} WHERE singleton;";
                if (step == TenantProvisioningStep.InitialAdministratorIdentityProvisioned && database.Service == "customer-identity")
                    sql = "SELECT EXISTS(SELECT 1 FROM customer_identity.tenant_principals WHERE actor_id='A900' AND normalized_email='a900@example.test');";
                if (step == TenantProvisioningStep.AccessManagementBootstrapped && database.Service == "am")
                    sql = """
                        SELECT EXISTS(
                            SELECT 1
                            FROM am.tenant_identity i
                            JOIN am.default_access_pack_history h
                              ON h.pack_version=i.default_access_pack_version
                            WHERE i.singleton
                              AND i.tenant_id=@tenant
                              AND i.initial_admin_actor_id='A900'
                              AND i.default_access_pack_version IS NOT NULL
                              AND h.pack_identity='monergy-default-access-pack/' || i.default_access_pack_version)
                            AND EXISTS(
                                SELECT 1 FROM am.access_subjects
                                WHERE actor_id='A900'
                                  AND administrative_authority='TenantAdmin'
                                  AND business_role_id IS NULL
                                  AND status='Active');
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
