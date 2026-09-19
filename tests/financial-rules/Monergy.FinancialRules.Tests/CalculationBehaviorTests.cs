using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Monergy.Contracts;
using Monergy.Services.FinancialRules.Application;
using Monergy.Services.FinancialRules.Domain;
using Monergy.Services.FinancialRules.Infrastructure;
using Xunit;

namespace Monergy.FinancialRules.Tests;

public sealed class CalculationBehaviorTests
{
    [Fact]
    public async Task ExecuteReadAndExplainPreserveExactAuthorityLineage()
    {
        var h = new Harness();
        await h.SeedAsync();
        var executed = Harness.Wire(await h.App.ExecuteAsync(h.Execute()));
        var result = Assert.IsType<CalculationResult>(executed.Data);
        Assert.Equal(125, result.Value);
        Assert.Equal(1, result.Revision);
        Assert.All(result.Inputs, input =>
        {
            Assert.Equal(1, input.FactRevision);
            Assert.Equal(input.FinancialFactId, input.Provenance.FinancialFactId);
            Assert.Equal("evidence", input.Provenance.EvidenceId);
            Assert.Equal("document-version", input.Provenance.DocumentVersionId);
            Assert.Equal("fixture-normalization/1", input.Provenance.NormalizationVersion);
        });
        Assert.Equal(result.DefinitionHash, h.Registry.Find(result.RuleId, result.RuleVersion)!.Definition.DefinitionHash);
        Assert.Equal(h.Security.Actor.ActorId, result.ActorId);
        Assert.Equal(h.Security.Workload.WorkloadIdentityId, result.WorkloadIdentityId);
        var read = Harness.Wire(await h.App.GetResultAsync(h.Get(result.CalculationResultId)));
        Assert.Equal(JsonSerializer.Serialize(result), JsonSerializer.Serialize(read.Data));
        var explanation = Harness.Wire(await h.App.ExplainAsync(h.Explain(result.CalculationResultId)));
        Assert.Contains("Not approved client methodology", explanation.Data!.Explanation, StringComparison.Ordinal);
        Assert.Equal(result.Operation, explanation.Data.Result.Operation);
        Assert.Contains("CID-032", h.Reader.Calls);
        Assert.Contains("CID-033", h.Reader.Calls);
        Assert.Single(h.Repository.PendingEvents());
    }

    [Fact]
    public async Task InputOrderingRequestIdsAndTimeDoNotCreateAnotherResultOnReplay()
    {
        var h = new Harness();
        await h.SeedAsync();
        var original = h.Execute();
        var first = await h.App.ExecuteAsync(original);
        h.Clock.Now = h.Clock.Now.AddMinutes(1);
        var second = await h.App.ExecuteAsync(original with
        {
            RequestId = "another-request",
            CorrelationId = "another-correlation",
            Payload = original.Payload with { Inputs = original.Payload.Inputs.Reverse().ToImmutableArray() }
        });
        Assert.Equal(first.Data, second.Data);
        Assert.Single(h.Repository.PendingEvents());
    }

    [Fact]
    public async Task SameIdentityDifferentPayloadConflictsAndNewKeyCreatesIndependentResult()
    {
        var h = new Harness();
        await h.SeedAsync();
        var first = await h.App.ExecuteAsync(h.Execute());
        var conflict = await h.App.ExecuteAsync(h.Execute(version: "2.0.0"));
        Assert.Equal(ContractErrorCategory.Conflict, conflict.Error?.Category);
        var next = await h.App.ExecuteAsync(h.Execute(key: "second", version: "2.0.0"));
        Assert.Equal(250, next.Data!.Value);
        Assert.NotEqual(first.Data!.CalculationResultId, next.Data.CalculationResultId);
        Assert.Equal(2, h.Repository.PendingEvents().Length);
    }

    [Fact]
    public async Task ConcurrentRetryConvergesToOneAtomicResultAndEvent()
    {
        var h = new Harness();
        await h.SeedAsync();
        var responses = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => h.App.ExecuteAsync(h.Execute()))));
        Assert.All(responses, response => Assert.Equal(ContractOutcome.Success, response.Outcome));
        Assert.Single(responses.Select(response => response.Data!.CalculationResultId).Distinct(StringComparer.Ordinal));
        Assert.Single(h.Repository.PendingEvents());
    }

    [Fact]
    public async Task FailedCommitPublishesNothingAndRetryCanSucceed()
    {
        var h = new Harness();
        await h.SeedAsync();
        h.Repository.FailNextCommit = true;
        var failed = await h.App.ExecuteAsync(h.Execute());
        Assert.Equal(ContractErrorCategory.TemporarilyUnavailable, failed.Error?.Category);
        Assert.True(failed.Error?.Retryable);
        Assert.Empty(h.Repository.PendingEvents());
        Assert.Equal(ContractOutcome.Success, (await h.App.ExecuteAsync(h.Execute())).Outcome);
        Assert.Single(h.Repository.PendingEvents());
    }

    [Fact]
    public async Task HistoricalReproductionNeverUsesLatestFactsOrLatestRule()
    {
        var h = new Harness();
        await h.SeedAsync();
        var initial = (await h.App.ExecuteAsync(h.Execute())).Data!;
        await h.SeedAsync(200, 50, 2);
        var historical = await h.App.ReproduceAsync(h.Explain(initial.CalculationResultId));
        Assert.Equal(initial, historical.Data);
        Assert.All(historical.Data!.Inputs, input => Assert.Equal(1, input.FactRevision));
        var replay = await h.App.ExecuteAsync(h.Execute());
        Assert.Equal(initial, replay.Data);
        var current = (await h.App.ExecuteAsync(h.Execute("new", version: "2.0.0"))).Data!;
        Assert.Equal(500, current.Value);
        Assert.All(current.Inputs, input => Assert.Equal(2, input.FactRevision));
        Assert.Equal(2, h.Repository.PendingEvents().Length);
    }

    [Fact]
    public async Task ExpectedInputRevisionRejectsChangedAuthorityWithoutEvent()
    {
        var h = new Harness();
        await h.SeedAsync();
        await h.SeedAsync(200, 50, 2);
        var request = h.Execute();
        request = request with { Payload = request.Payload with { Inputs = request.Payload.Inputs.Select(input => input with { ExpectedRevision = 1 }).ToImmutableArray() } };
        Assert.Equal(ContractErrorCategory.PreconditionFailed, (await h.App.ExecuteAsync(request)).Error?.Category);
        Assert.Empty(h.Repository.PendingEvents());
    }

    [Fact]
    public async Task FactAndProvenanceRemainPairedIfAuthorityChangesBetweenReads()
    {
        var h = new Harness();
        await h.SeedAsync();
        var changed = false;
        h.Reader.BeforeProvenance = () =>
        {
            if (changed) { return; }
            changed = true;
            h.SeedAsync(200, 50, 2).GetAwaiter().GetResult();
        };
        var result = (await h.App.ExecuteAsync(h.Execute())).Data!;
        Assert.Equal(1, result.Inputs[0].FactRevision);
        Assert.Equal(100, result.Inputs[0].Value);
        Assert.Contains("-1", result.Inputs[0].Provenance.SourceFactId, StringComparison.Ordinal);
        Assert.Equal(2, result.Inputs[1].FactRevision);
        Assert.Equal(50, result.Inputs[1].Value);
        // Per-input consistency is explicit; this is not a distributed snapshot.
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("drift")]
    [InlineData("unavailable")]
    public async Task HistoricalAccessFailsClosedWithoutFallback(string failure)
    {
        var h = new Harness();
        await h.SeedAsync();
        var result = (await h.App.ExecuteAsync(h.Execute())).Data!;
        h.Reader.MissingProvenance = failure == "missing";
        h.Reader.Drift = failure == "drift";
        h.Reader.Unavailable = failure == "unavailable";
        Assert.NotEqual(ContractOutcome.Success, (await h.App.GetResultAsync(h.Get(result.CalculationResultId))).Outcome);
        Assert.NotEqual(ContractOutcome.Success, (await h.App.ExplainAsync(h.Explain(result.CalculationResultId))).Outcome);
        Assert.NotEqual(ContractOutcome.Success, (await h.App.ReproduceAsync(h.Explain(result.CalculationResultId))).Outcome);
        Assert.NotEqual(ContractOutcome.Success, (await h.App.ExecuteAsync(h.Execute())).Outcome);
        Assert.Single(h.Repository.PendingEvents());
    }

    [Theory]
    [InlineData("authorization-revoked")]
    [InlineData("consent-revoked")]
    [InlineData("authorization-expired")]
    [InlineData("consent-expired")]
    public async Task CurrentPolicyAppliesToExecuteReadExplainReplayAndReproduction(string mode)
    {
        var h = new Harness();
        await h.SeedAsync();
        var result = (await h.App.ExecuteAsync(h.Execute())).Data!;
        if (mode == "authorization-revoked") { h.Grant(revoked: true); }
        if (mode == "consent-revoked") { h.Grant(consentRevoked: true); }
        if (mode == "authorization-expired") { h.Grant(expiry: h.Clock.Now); }
        if (mode == "consent-expired") { h.Clock.Now = h.Clock.Now.AddMinutes(31); }
        Assert.NotEqual(ContractOutcome.Success, (await h.App.ExecuteAsync(h.Execute("new"))).Outcome);
        Assert.NotEqual(ContractOutcome.Success, (await h.App.ExecuteAsync(h.Execute())).Outcome);
        Assert.NotEqual(ContractOutcome.Success, (await h.App.GetResultAsync(h.Get(result.CalculationResultId))).Outcome);
        Assert.NotEqual(ContractOutcome.Success, (await h.App.ExplainAsync(h.Explain(result.CalculationResultId))).Outcome);
        Assert.NotEqual(ContractOutcome.Success, (await h.App.ReproduceAsync(h.Explain(result.CalculationResultId))).Outcome);
        Assert.Single(h.Repository.PendingEvents());
    }

    [Fact]
    public async Task RevocationDuringInputReadsPreventsCommit()
    {
        var h = new Harness();
        await h.SeedAsync();
        h.Reader.BeforeProvenance = () => h.Grant(consentRevoked: true);
        Assert.Equal(ContractErrorCategory.ConsentRevoked, (await h.App.ExecuteAsync(h.Execute())).Error?.Category);
        Assert.Empty(h.Repository.PendingEvents());
    }

    [Theory]
    [InlineData("actor")]
    [InlineData("workload")]
    [InlineData("purpose")]
    [InlineData("customer")]
    [InlineData("authorization")]
    [InlineData("consent")]
    public async Task CallerCannotForgeTrustedContext(string field)
    {
        var h = new Harness();
        await h.SeedAsync();
        var security = field switch
        {
            "actor" => h.Security with { Actor = h.Security.Actor with { ActorId = "forged" } },
            "workload" => h.Security with { Workload = h.Security.Workload with { WorkloadIdentityId = "forged" } },
            "purpose" => h.Security with { Access = h.Security.Access with { Purpose = "forged" } },
            "customer" => h.Security with { Access = h.Security.Access with { CustomerId = "forged" } },
            "authorization" => h.Security with { Access = h.Security.Access with { AuthorizationContextId = "forged" } },
            _ => h.Security with { Access = h.Security.Access with { ConsentReferenceId = null } },
        };
        var response = await h.App.ExecuteAsync(h.Execute() with { Security = security });
        Assert.NotEqual(ContractOutcome.Success, response.Outcome);
        Assert.Empty(h.Repository.PendingEvents());
    }

    [Fact]
    public async Task DifferentAuthorizedCustomerCannotReadAnotherCustomersResult()
    {
        var h = new Harness();
        await h.SeedAsync();
        var result = (await h.App.ExecuteAsync(h.Execute())).Data!;
        var other = h.Security with { Access = h.Security.Access with { CustomerId = "other", AuthorizationContextId = "other-auth" } };
        h.Grant(security: other);
        var query = h.Request(FinancialRulesContractNames.GetCalculationResult, new GetCalculationResult("other", result.CalculationResultId), security: other);
        Assert.Equal(ContractErrorCategory.NotFound, (await h.App.GetResultAsync(query)).Error?.Category);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("name")]
    [InlineData("key")]
    [InlineData("empty")]
    [InlineData("duplicate")]
    [InlineData("oversized")]
    [InlineData("null-security")]
    [InlineData("null-payload")]
    [InlineData("revision")]
    public async Task InvalidCommandsAreRejectedBeforeCommit(string mode)
    {
        var h = new Harness();
        await h.SeedAsync();
        var request = h.Execute();
        request = mode switch
        {
            "version" => request with { ContractVersion = "99" },
            "name" => request with { ContractName = "Other" },
            "key" => request with { IdempotencyKey = null },
            "empty" => request with { Payload = request.Payload with { Inputs = [] } },
            "duplicate" => request with { Payload = request.Payload with { Inputs = [request.Payload.Inputs[0], request.Payload.Inputs[0]] } },
            "oversized" => request with { Payload = request.Payload with { Inputs = Enumerable.Range(0, 33).Select(i => new CalculationInputReference(i.ToString(CultureInfo.InvariantCulture), "fact", null)).ToImmutableArray() } },
            "null-security" => request with { Security = null! },
            "null-payload" => request with { Payload = null! },
            _ => request with { Payload = request.Payload with { Inputs = [request.Payload.Inputs[0] with { ExpectedRevision = 0 }] } },
        };
        Assert.NotEqual(ContractOutcome.Success, (await h.App.ExecuteAsync(request)).Outcome);
        Assert.Empty(h.Repository.PendingEvents());
    }

    [Fact]
    public async Task MissingVersionDoesNotFallbackAndConflictingRegistrationCannotReplaceIdentity()
    {
        var h = new Harness();
        await h.SeedAsync();
        var original = h.Registry.Find("engineering.sum", "1.0.0")!;
        h.Registry.Register(new EngineeringRule("engineering.sum", "1.0.0"));
        Assert.Throws<InvalidOperationException>(() => h.Registry.Register(new ChangedRule(original)));
        Assert.Same(original, h.Registry.Find("engineering.sum", "1.0.0"));
        Assert.Equal(ContractErrorCategory.NotFound, (await h.App.ExecuteAsync(h.Execute(version: "3.0.0"))).Error?.Category);
        var initial = (await h.App.ExecuteAsync(h.Execute())).Data!;
        var noRules = new FinancialRulesApplication(new ReferenceRuleRegistry(Harness.Config()), h.Reader, h.Policy, h.Repository, h.Telemetry, h.Clock);
        Assert.Equal(ContractErrorCategory.NotFound, (await noRules.ReproduceAsync(h.Explain(initial.CalculationResultId))).Error?.Category);
    }

    [Theory]
    [InlineData("DEV")]
    [InlineData("QA")]
    [InlineData("UAT")]
    [InlineData("PRODUCTION")]
    [InlineData("")]
    [InlineData("SIMULATOR")]
    public void PersistentOrUnknownZonesCannotActivateAnyReferenceAdapter(string zone)
    {
        Assert.Throws<InvalidOperationException>(() => new ReferenceRuleRegistry(Harness.Config(zone)));
        Assert.Throws<InvalidOperationException>(() => new InMemoryCalculationRepository(Harness.Config(zone)));
        Assert.Throws<InvalidOperationException>(() => new ReferenceCalculationAccessPolicy(Harness.Config(zone), TimeProvider.System));
    }

    [Theory]
    [InlineData("LOCAL")]
    [InlineData("CI_EPHEMERAL")]
    [InlineData("CI/EPHEMERAL")]
    public void ExistingReferenceZoneSpellingsRemainSupported(string zone)
    {
        Assert.NotNull(new ReferenceRuleRegistry(Harness.Config(zone)));
        Assert.NotNull(new InMemoryCalculationRepository(Harness.Config(zone)));
    }

    private sealed class ChangedRule(IDeterministicRule original) : IDeterministicRule
    {
        public RuleDefinition Definition => original.Definition with { NumericSemantics = "changed" };
        public EvaluatedValue Evaluate(ImmutableArray<CalculationInputLineage> inputs) => original.Evaluate(inputs);
    }

    [Fact]
    public async Task HistoricalReproductionRejectsChangedOriginalDefinition()
    {
        var h = new Harness();
        await h.SeedAsync();
        var result = (await h.App.ExecuteAsync(h.Execute())).Data!;
        var drifted = new SingleRuleRegistry(new ChangedRule(h.Registry.Find(result.RuleId, result.RuleVersion)!));
        var app = new FinancialRulesApplication(drifted, h.Reader, h.Policy, h.Repository, h.Telemetry, h.Clock);
        var reproduced = await app.ReproduceAsync(h.Explain(result.CalculationResultId));
        Assert.Equal("calculation.rule.drift", reproduced.Error?.Code);
        Assert.Null(reproduced.Data);
        Assert.Single(h.Repository.PendingEvents());
        Assert.Equal(result, (await h.App.GetResultAsync(h.Get(result.CalculationResultId))).Data);
    }

    private sealed class SingleRuleRegistry(IDeterministicRule rule) : IRuleRegistry
    {
        public IDeterministicRule? Find(string ruleId, string version) =>
            rule.Definition.RuleId == ruleId && rule.Definition.Version == version ? rule : null;
    }
}
