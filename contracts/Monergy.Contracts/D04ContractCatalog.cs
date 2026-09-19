namespace Monergy.Contracts;

public sealed record D04ContractDefinition(
    string Id,
    string Name,
    string Type,
    string Owner,
    string Producer,
    IReadOnlyList<string> Consumers,
    string Input,
    string Output,
    string StateSemantics,
    string ErrorSemantics,
    string Authorization,
    string Idempotency,
    string Provenance,
    string Versioning);

public static class D04ContractCatalog
{
    private const string Owner = "Financial Profile Service";
    private const string Versioning = "Semantic version 1.0.0; additive-compatible evolution only within the major version.";
    private const string Authorization = "The receiving Financial Profile Service enforces trusted workload, actor, purpose, authorization and customer context server-side.";

    public static IReadOnlyList<D04ContractDefinition> All { get; } =
    [
        new(
            "CID-030",
            Vs02ContractNames.GetFinancialProfile,
            "QUERY",
            Owner,
            Owner,
            ["Search & Retrieval Service", "Reporting Service", "Financial Rules Service"],
            "Authorized financial-profile and customer identifiers.",
            "Provider-neutral current Financial Profile projection.",
            "Read-only current-state projection; absence and denial are not conflated with mutable state.",
            "VALIDATION_ERROR, AUTHENTICATION_REQUIRED, ACCESS_DENIED, NOT_FOUND and UNSUPPORTED_OPERATION.",
            Authorization,
            "READ_ONLY",
            "Each returned fact carries its Financial Provenance reference.",
            Versioning),
        new(
            "CID-031",
            Vs02ContractNames.NormalizeSourceFacts,
            "COMMAND",
            Owner,
            Owner,
            ["Document Intelligence Service", "Integration Gateway Service"],
            "Validated provider-neutral source facts with evidence and transformation references.",
            "Created or revised authoritative financial facts.",
            "Financial Profile alone promotes facts atomically and records explicit revisions; replay has no duplicate effect.",
            "VALIDATION_ERROR, AUTHENTICATION_REQUIRED, ACCESS_DENIED, CONFLICT and UNSUPPORTED_OPERATION.",
            Authorization,
            "IDEMPOTENCY_KEY_REQUIRED",
            "Evidence, document version, source fact, extraction, validation and normalization versions are retained.",
            Versioning),
        new(
            "CID-032",
            Vs02ContractNames.GetFinancialFact,
            "QUERY",
            Owner,
            Owner,
            ["Financial Rules Service", "Search & Retrieval Service", "Reporting Service"],
            "Authorized financial-fact and customer identifiers.",
            "One current authoritative provider-neutral financial fact.",
            "Read-only current fact revision; history is not destructively rewritten.",
            "VALIDATION_ERROR, AUTHENTICATION_REQUIRED, ACCESS_DENIED, NOT_FOUND and UNSUPPORTED_OPERATION.",
            Authorization,
            "READ_ONLY",
            "The response carries its Financial Provenance reference.",
            Versioning),
        new(
            "CID-033",
            Vs02ContractNames.GetFinancialProvenance,
            "QUERY",
            Owner,
            Owner,
            ["Financial Rules Service", "Search & Retrieval Service", "Reporting Service"],
            "Authorized Financial Provenance and customer identifiers.",
            "Immutable historical Financial Provenance.",
            "Read-only immutable lineage lookup.",
            "VALIDATION_ERROR, AUTHENTICATION_REQUIRED, ACCESS_DENIED, NOT_FOUND and UNSUPPORTED_OPERATION.",
            Authorization,
            "READ_ONLY",
            "The response resolves evidence, source-fact, transformation, actor, workload, time and correlation lineage.",
            Versioning),
        new(
            "CID-034",
            Vs02ContractNames.FinancialFactCreated,
            "EVENT",
            Owner,
            Owner,
            ["Financial Rules Service", "Search & Retrieval Service", "Audit Service"],
            "A committed authoritative fact creation.",
            "Immutable created-fact identity, type, revision and provenance reference.",
            "Describes an already committed fact and never carries a mutable profile replica.",
            "Malformed or unsupported events are rejected by consumers without changing authoritative state.",
            "Consumers apply their own authorization where they query current detail.",
            "IMMUTABLE_REPLAY_SAFE",
            "Carries the committed Financial Provenance reference.",
            Versioning),
        new(
            "CID-035",
            Vs02ContractNames.FinancialFactUpdated,
            "EVENT",
            Owner,
            Owner,
            ["Financial Rules Service", "Search & Retrieval Service", "Audit Service"],
            "A committed authoritative fact revision.",
            "Immutable updated-fact identity, type, revision and provenance reference.",
            "Describes an already committed revision; prior fact and provenance history remain intact.",
            "Malformed or unsupported events are rejected by consumers without changing authoritative state.",
            "Consumers apply their own authorization where they query current detail.",
            "IMMUTABLE_REPLAY_SAFE",
            "Carries the new immutable Financial Provenance reference.",
            Versioning),
        new(
            "CID-036",
            Vs02ContractNames.FinancialProfileChanged,
            "EVENT",
            Owner,
            Owner,
            ["Financial Rules Service", "Search & Retrieval Service", "Reporting Service"],
            "A committed Financial Profile aggregate change.",
            "Coarse immutable profile revision and changed fact identifiers.",
            "Prompts authorized current-state query and does not replicate the profile.",
            "Malformed or unsupported events are rejected by consumers without changing authoritative state.",
            "Consumers apply their own authorization where they query current detail.",
            "IMMUTABLE_REPLAY_SAFE",
            "Fact-level events carry provenance; this coarse notification carries identifiers only.",
            Versioning),
    ];
}

public static class FinancialObjectTypes
{
    public const string Income = "INCOME";
    public const string Expense = "EXPENSE";
    public const string FinancialTransaction = "FINANCIAL_TRANSACTION";
    public const string BankAccount = "BANK_ACCOUNT";
    public const string Loan = "LOAN";
    public const string CreditCardAccount = "CREDIT_CARD_ACCOUNT";
    public const string CreditProfile = "CREDIT_PROFILE";
    public const string Investment = "INVESTMENT";
    public const string InsurancePolicy = "INSURANCE_POLICY";
    public const string Property = "PROPERTY";
    public const string Vehicle = "VEHICLE";
    public const string TaxRecord = "TAX_RECORD";
    public const string FinancialGoal = "FINANCIAL_GOAL";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Income,
        Expense,
        FinancialTransaction,
        BankAccount,
        Loan,
        CreditCardAccount,
        CreditProfile,
        Investment,
        InsurancePolicy,
        Property,
        Vehicle,
        TaxRecord,
        FinancialGoal,
    };
}
