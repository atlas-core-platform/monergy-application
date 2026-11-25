using System.Collections.Concurrent;
using Xunit;

namespace Monergy.UatGateway;

public sealed class DiagnosticModeTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void StartsBoundedSessionForActualPlatformPrincipal()
    {
        var store = Store(out _);
        var session = store.Start("Investigate bounded owner timeout", 15, "A950");
        Assert.Matches("^diag-[0-9a-f]{48}$", session.Id);
        Assert.Equal("A950", session.StartedBy);
        Assert.Equal(Now.AddMinutes(15), session.ExpiresAt);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(31)]
    public void RejectsDurationOutsideContract(int minutes) =>
        Assert.Throws<InvalidOperationException>(() => Store(out _).Start("Investigate bounded owner timeout", minutes, "A950"));

    [Theory]
    [InlineData("short")]
    [InlineData("Investigate token=not-allowed")]
    [InlineData("Investigate customer id C001")]
    public void RejectsUnboundedOrSensitiveReason(string reason) =>
        Assert.Throws<InvalidOperationException>(() => Store(out _).Start(reason, 15, "A950"));

    [Fact]
    public void StopIsIdempotentAndTerminal()
    {
        var store = Store(out _); var session = store.Start("Investigate bounded owner timeout", 15, "A950");
        var first = store.Stop(session.Id); var second = store.Stop(session.Id);
        Assert.Equal(first.StoppedAt, second.StoppedAt);
        Assert.Throws<InvalidOperationException>(() => store.AddEvidence(session.Id, Evidence(session.Id, 1)));
    }

    [Fact]
    public void ExpiredSessionRejectsEvidence()
    {
        var store = Store(out var clock); var session = store.Start("Investigate bounded owner timeout", 5, "A950");
        clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Throws<InvalidOperationException>(() => store.AddEvidence(session.Id, Evidence(session.Id, 1)));
    }

    [Fact]
    public void EvidenceIsClosedBoundedAndDeduplicated()
    {
        var store = Store(out _); var session = store.Start("Investigate bounded owner timeout", 15, "A950");
        var item = Evidence(session.Id, 1);
        Assert.Single(store.AddEvidence(session.Id, item).Evidence);
        Assert.Single(store.AddEvidence(session.Id, item).Evidence);
        for (var index = 2; index <= DiagnosticSessionStore.MaximumEvidence; index++) store.AddEvidence(session.Id, Evidence(session.Id, index));
        Assert.Throws<InvalidOperationException>(() => store.AddEvidence(session.Id, Evidence(session.Id, 999)));
    }

    [Fact]
    public void EvidenceMustBindToRouteSessionAndExactSchema()
    {
        var store = Store(out _); var session = store.Start("Investigate bounded owner timeout", 15, "A950");
        Assert.Throws<InvalidOperationException>(() => store.AddEvidence(session.Id, Evidence("diag-" + new string('2', 48), 1)));
        Assert.Throws<InvalidOperationException>(() => store.AddEvidence(session.Id, Evidence(session.Id, 1, true)));
    }

    [Fact]
    public async Task ConcurrentStopAndIngestionNeverLoseTerminalState()
    {
        for (var iteration = 0; iteration < 100; iteration++)
        {
            var store = Store(out _); var session = store.Start("Investigate bounded owner timeout", 15, "A950");
            var failures = new ConcurrentBag<Exception>();
            await Task.WhenAll(
                Task.Run(() => { try { store.Stop(session.Id); } catch (Exception failure) { failures.Add(failure); } }),
                Task.Run(() => { try { store.AddEvidence(session.Id, Evidence(session.Id, iteration + 1)); } catch (InvalidOperationException) { } catch (Exception failure) { failures.Add(failure); } }));
            Assert.NotNull(store.Require(session.Id).StoppedAt);
            Assert.Throws<InvalidOperationException>(() => store.AddEvidence(session.Id, Evidence(session.Id, 500 + iteration)));
            Assert.Empty(failures);
        }
    }

    private static DiagnosticSessionStore Store(out MutableClock clock)
    {
        clock = new MutableClock(Now);
        return new DiagnosticSessionStore(clock);
    }

    private static DiagnosticEvidenceRequest Evidence(string id, int sequence, bool unknown = false) => new()
    {
        DiagnosticSessionId = id,
        Service = "uat-gateway",
        Operation = "owner-call",
        Outcome = sequence % 2 == 0 ? "SUCCESS" : "FAILURE",
        ReleaseCommit = new string('a', 40),
        Environment = "LOCAL",
        OccurredAt = Now,
        Classification = "TIMEOUT",
        CorrelationId = $"corr-{sequence}",
        DurationMs = sequence,
        Unknown = unknown ? new() { ["customerId"] = default } : null,
    };

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan value) => now += value;
    }
}
