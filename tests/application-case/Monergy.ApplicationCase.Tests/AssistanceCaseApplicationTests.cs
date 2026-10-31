using Monergy.Services.ApplicationCase.Application;
using Xunit;

namespace Monergy.ApplicationCase.Tests;

public sealed class AssistanceCaseApplicationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CustomerCreatesOneActorBoundCaseAndReplayIsIdempotent()
    {
        using var owner = Owner();
        var context = Customer();
        var first = await owner.CreateAsync(context, new(Op('a')), Confirm(context), default);
        var replay = await owner.CreateAsync(context, new(Op('a')), Confirm(context), default);
        var secondOperation = await owner.CreateAsync(context, new(Op('b')), Confirm(context), default);

        Assert.Equal(first, replay);
        Assert.Equal(first.CaseId, secondOperation.CaseId);
        Assert.Equal("Submitted", first.State);
        Assert.False(first.AssignedToMe);
        Assert.Single(await owner.ListCustomerAsync(context, default));
    }

    [Fact]
    public async Task CustomerCreateFailsWhenCurrentAuthorityChanges()
    {
        using var owner = Owner();
        var context = Customer();
        var changed = context with { SubjectVersion = 2 };

        var failure = await Assert.ThrowsAsync<ApplicationCaseException>(() =>
            owner.CreateAsync(context, new(Op('a')), Confirm(changed), default));

        Assert.Equal("CASE_AUTHORITY_CHANGED", failure.Code);
        Assert.Empty(await owner.ListCustomerAsync(context, default));
    }

    [Fact]
    public async Task CustomerAndDsaCapabilitiesCannotBeSubstituted()
    {
        using var owner = Owner();
        var customerWithDsaCapability = Customer() with
        { CapabilityId = AssistanceCaseApplication.DsaCapability };
        var dsaWithCustomerCapability = Dsa() with
        { CapabilityId = AssistanceCaseApplication.CustomerCapability };

        Assert.Equal("CUSTOMER_CASE_ACCESS_REQUIRED", (await Assert.ThrowsAsync<ApplicationCaseException>(() =>
            owner.CreateAsync(customerWithDsaCapability, new(Op('a')), Confirm(customerWithDsaCapability), default))).Code);
        Assert.Equal("DSA_CASE_ACCESS_REQUIRED", (await Assert.ThrowsAsync<ApplicationCaseException>(() =>
            owner.ListDsaAsync(dsaWithCustomerCapability, default))).Code);
    }

    [Fact]
    public async Task TenantIsolationHidesCasesAndHistory()
    {
        using var owner = Owner();
        var created = await owner.CreateAsync(Customer(), new(Op('a')), Confirm(Customer()), default);
        var foreignCustomer = Customer() with { TenantId = "T002", CustomerId = "C002", ActorId = "A200" };
        var foreignDsa = Dsa() with { TenantId = "T002", ActorId = "D200" };

        Assert.Empty(await owner.ListCustomerAsync(foreignCustomer, default));
        Assert.Empty(await owner.ListDsaAsync(foreignDsa, default));
        Assert.Equal("CASE_NOT_FOUND", (await Assert.ThrowsAsync<ApplicationCaseException>(() =>
            owner.HistoryAsync(created.CaseId, foreignCustomer, default))).Code);
    }

    [Fact]
    public async Task DsaAcceptsSubmittedCaseAndCustomerSeesLifecycle()
    {
        using var owner = Owner();
        var customer = Customer();
        var dsa = Dsa();
        var created = await owner.CreateAsync(customer, new(Op('a')), Confirm(customer), default);

        var accepted = await owner.AcceptAsync(created.CaseId, dsa,
            new(Op('b'), created.Version), Confirm(dsa), default);
        var customerView = Assert.Single(await owner.ListCustomerAsync(customer, default));
        var dsaView = Assert.Single(await owner.ListDsaAsync(dsa, default));
        var history = await owner.HistoryAsync(created.CaseId, customer, default);

        Assert.Equal("Accepted", accepted.State);
        Assert.True(accepted.AssignedToMe);
        Assert.False(customerView.AssignedToMe);
        Assert.Equal(accepted.CaseId, dsaView.CaseId);
        Assert.Equal(["Submitted", "Accepted"], history.Events.Select(value => value.Action));
        Assert.Equal([1, 2], history.Events.Select(value => value.Version));
    }

    [Fact]
    public async Task AcceptReplayIsIdempotentButChangedReuseConflicts()
    {
        using var owner = Owner();
        var customer = Customer();
        var dsa = Dsa();
        var created = await owner.CreateAsync(customer, new(Op('a')), Confirm(customer), default);
        var first = await owner.AcceptAsync(created.CaseId, dsa, new(Op('b'), 1), Confirm(dsa), default);
        var replay = await owner.AcceptAsync(created.CaseId, dsa, new(Op('b'), 1), Confirm(dsa), default);

        Assert.Equal(first, replay);
        Assert.Equal("OPERATION_REUSE_CONFLICT", (await Assert.ThrowsAsync<ApplicationCaseException>(() =>
            owner.CreateAsync(customer with { ActorId = dsa.ActorId }, new(Op('b')),
                Confirm(customer with { ActorId = dsa.ActorId }), default))).Code);
    }

    [Fact]
    public async Task StaleVersionFailsBeforeAcceptance()
    {
        using var owner = Owner();
        var created = await owner.CreateAsync(Customer(), new(Op('a')), Confirm(Customer()), default);

        var failure = await Assert.ThrowsAsync<ApplicationCaseException>(() =>
            owner.AcceptAsync(created.CaseId, Dsa(), new(Op('b'), 2), Confirm(Dsa()), default));

        Assert.Equal("CASE_VERSION_CHANGED", failure.Code);
        Assert.Equal("Submitted", Assert.Single(await owner.ListDsaAsync(Dsa(), default)).State);
    }

    [Fact]
    public async Task DsaAuthorityLossFailsBeforeAcceptance()
    {
        using var owner = Owner();
        var created = await owner.CreateAsync(Customer(), new(Op('a')), Confirm(Customer()), default);
        var dsa = Dsa();

        var failure = await Assert.ThrowsAsync<ApplicationCaseException>(() => owner.AcceptAsync(
            created.CaseId, dsa, new(Op('b'), 1), Confirm(dsa with { PolicyVersion = 2 }), default));

        Assert.Equal("CASE_AUTHORITY_CHANGED", failure.Code);
    }

    [Fact]
    public async Task ConcurrentAcceptanceHasOneWinner()
    {
        using var owner = Owner();
        var created = await owner.CreateAsync(Customer(), new(Op('a')), Confirm(Customer()), default);
        var first = Dsa();
        var second = Dsa() with { ActorId = "D301" };
        var attempts = await Task.WhenAll(
            Attempt(() => owner.AcceptAsync(created.CaseId, first, new(Op('b'), 1), Confirm(first), default)),
            Attempt(() => owner.AcceptAsync(created.CaseId, second, new(Op('c'), 1), Confirm(second), default)));

        Assert.Single(attempts, value => value is AssistanceCaseView);
        Assert.Single(attempts, value => value is ApplicationCaseException { Code: "CASE_ALREADY_ACCEPTED" });
    }

    [Fact]
    public async Task InvalidOperationAndCaseIdentifiersFailClosed()
    {
        using var owner = Owner();
        Assert.Equal("OPERATION_ID_INVALID", (await Assert.ThrowsAsync<ApplicationCaseException>(() =>
            owner.CreateAsync(Customer(), new("not-an-operation"), Confirm(Customer()), default))).Code);
        Assert.Equal("CASE_ID_INVALID", (await Assert.ThrowsAsync<ApplicationCaseException>(() =>
            owner.AcceptAsync("bad/id", Dsa(), new(Op('a'), 1), Confirm(Dsa()), default))).Code);
    }

    private static AssistanceCaseApplication Owner() => new(new FixedTimeProvider(Now));
    private static CaseActorContext Customer() => new("T001", "A100", "C001", 1, 1,
        AssistanceCaseApplication.CustomerCapability);
    private static CaseActorContext Dsa() => new("T001", "D300", null, 1, 1,
        AssistanceCaseApplication.DsaCapability);
    private static string Op(char value) => new(value, 32);
    private static Func<CancellationToken, Task<CaseActorContext>> Confirm(CaseActorContext value) =>
        _ => Task.FromResult(value);
    private static async Task<object> Attempt(Func<Task<AssistanceCaseView>> action)
    {
        try { return await action(); }
        catch (ApplicationCaseException failure) { return failure; }
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
