using Monergy.Contracts;
using Xunit;

namespace Monergy.CustomerIdentity.Tests;

public sealed class CustomerAuthorityTests
{
    [Fact]
    public async Task FirstCustomerCreateAndIdenticalRedeliveryReconcileOneAuthoritativeEffect()
    {
        var harness = new D13Harness();
        var request = harness.Register();

        var first = await harness.App.RegisterOrUpdateCustomerAsync(request);
        harness.Clock.Now = harness.Clock.Now.AddHours(1);
        var replay = await harness.App.RegisterOrUpdateCustomerAsync(request);

        Assert.Equal(ContractOutcome.Success, first.Outcome);
        Assert.Equal(first.Data, replay.Data);
        Assert.Equal(1, harness.Repository.CustomerAuthoritativeEffectCount);
    }

    [Fact]
    public async Task CustomerIdempotencyKeyReuseWithChangedSemanticPayloadIsRejected()
    {
        var harness = new D13Harness();
        await harness.App.RegisterOrUpdateCustomerAsync(harness.Register());

        var conflict = await harness.App.RegisterOrUpdateCustomerAsync(
            harness.Register(displayName: "Changed Reference Name"));

        Assert.Equal(ContractOutcome.Rejected, conflict.Outcome);
        Assert.Equal(ContractErrorCategory.DuplicateRequest, conflict.Error?.Category);
        Assert.Equal(1, harness.Repository.CustomerAuthoritativeEffectCount);
    }

    [Fact]
    public async Task ConcurrentIdenticalCustomerMutationHasOneWinnerAndImmutableSnapshots()
    {
        var harness = new D13Harness();
        var request = harness.Register();
        var tasks = Enumerable.Range(0, 32)
            .Select(_ => Task.Run(() => harness.App.RegisterOrUpdateCustomerAsync(request)))
            .ToArray();

        var results = await Task.WhenAll(tasks);
        Assert.All(results, result => Assert.Equal(ContractOutcome.Success, result.Outcome));
        Assert.All(results, result => Assert.Equal(results[0].Data, result.Data));
        Assert.Equal(1, harness.Repository.CustomerAuthoritativeEffectCount);

        var callerCopy = results[0].Data! with { DisplayName = "Caller changed copy" };
        var stored = await harness.App.GetCustomerAsync(harness.Get());
        Assert.NotEqual(callerCopy.DisplayName, stored.Data?.DisplayName);
        Assert.Equal(D13Harness.DisplayName, stored.Data?.DisplayName);
    }

    [Fact]
    public async Task CustomerUpdateRequiresExactRevisionAndCannotSilentlyOverwrite()
    {
        var harness = new D13Harness();
        await harness.CreateCustomerAsync();
        var update = await harness.App.RegisterOrUpdateCustomerAsync(
            harness.Register("customer-update-1", "First Authorized Update", expectedRevision: 1));
        var stale = await harness.App.RegisterOrUpdateCustomerAsync(
            harness.Register("customer-update-2", "Stale Overwrite", expectedRevision: 1));
        var stored = await harness.App.GetCustomerAsync(harness.Get());

        Assert.Equal(2, update.Data?.Revision);
        Assert.Equal(ContractErrorCategory.Conflict, stale.Error?.Category);
        Assert.Equal("First Authorized Update", stored.Data?.DisplayName);
        Assert.Equal(2, harness.Repository.CustomerAuthoritativeEffectCount);
    }

    [Fact]
    public async Task SameCustomerReadSucceedsCrossCustomerFailsClosedAndMissingIsNotFound()
    {
        var harness = new D13Harness();
        await harness.CreateCustomerAsync();
        var sameCustomer = await harness.App.GetCustomerAsync(harness.Get());
        var crossCustomer = await harness.App.GetCustomerAsync(
            harness.Get(security: D13Harness.SecurityFor("customer-b")));
        var missing = await harness.App.GetCustomerAsync(
            harness.Get("customer-missing", D13Harness.SecurityFor("customer-missing")));

        Assert.Equal(ContractOutcome.Success, sameCustomer.Outcome);
        Assert.Equal(ContractErrorCategory.AccessDenied, crossCustomer.Error?.Category);
        Assert.Equal(ContractErrorCategory.NotFound, missing.Error?.Category);
        Assert.Null(crossCustomer.Data);
    }

    [Fact]
    public async Task CustomerReadDependencyFailureRemainsDistinctFromAbsenceDenialAndInvalidInput()
    {
        var harness = new D13Harness();
        harness.Repository.FailNextCustomerRead();
        var dependency = await harness.App.GetCustomerAsync(harness.Get());
        var invalid = await harness.App.GetCustomerAsync(harness.Get(" customer-a",
            D13Harness.SecurityFor(" customer-a")));

        Assert.Equal(ContractOutcome.Failed, dependency.Outcome);
        Assert.Equal(ContractErrorCategory.DependencyFailure, dependency.Error?.Category);
        Assert.Equal(ContractErrorCategory.ValidationError, invalid.Error?.Category);
    }
}
