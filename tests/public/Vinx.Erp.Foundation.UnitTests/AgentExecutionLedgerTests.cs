using Vinx.Erp.Foundation.Domain;
using Xunit;

namespace Vinx.Erp.Foundation.UnitTests;

public sealed class AgentExecutionLedgerTests
{
    [Fact]
    public void Model_in_flight_cannot_move_back_to_vision()
    {
        var actorId = Guid.Parse("11111111-1111-4111-8111-111111111111");
        var requestId = Guid.Parse("22222222-2222-4222-8222-222222222222");
        var ledger = CreateLedger(actorId, requestId);

        ledger.MarkVisionInFlight(actorId, requestId);
        ledger.MarkModelInFlight(actorId, requestId);

        Assert.Throws<InvalidOperationException>(() =>
            ledger.MarkVisionInFlight(actorId, requestId));
    }

    [Fact]
    public void Lifecycle_requires_the_exact_monotonic_state_order()
    {
        var actorId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var ledger = CreateLedger(actorId, requestId);

        Assert.Equal(AgentExecutionLedgerState.Claimed, ledger.State);
        Assert.Throws<InvalidOperationException>(() => ledger.MarkModelInFlight(actorId, requestId));
        Assert.Throws<InvalidOperationException>(() => ledger.Complete("{}", actorId, requestId));
        Assert.Throws<InvalidOperationException>(() => ledger.FailClosed("provider_unavailable", actorId, requestId));
        Assert.Throws<InvalidOperationException>(() => ledger.MarkAmbiguous("provider_timeout", actorId, requestId));

        ledger.MarkVisionInFlight(actorId, requestId);
        ledger.MarkModelInFlight(actorId, requestId);
        ledger.FailClosed("provider_unavailable", actorId, requestId);

        Assert.Equal(AgentExecutionLedgerState.FailedClosed, ledger.State);
        Assert.Throws<InvalidOperationException>(() => ledger.MarkModelInFlight(actorId, requestId));
    }

    [Fact]
    public void Terminal_result_is_available_only_after_successful_completion()
    {
        var actorId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var ledger = CreateLedger(actorId, requestId);

        Assert.Null(ledger.SafeResultJson);

        ledger.MarkVisionInFlight(actorId, requestId);
        ledger.MarkModelInFlight(actorId, requestId);
        ledger.Complete("""{"outcome":"completed"}""", actorId, requestId);

        Assert.Equal(AgentExecutionLedgerState.Completed, ledger.State);
        Assert.Equal("""{"outcome":"completed"}""", ledger.SafeResultJson);
        Assert.Null(ledger.ReasonCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF01234567")]
    [InlineData("abcdef0123456789abcdef0123456789abcdef0123456789abcdef012345678")]
    public void Create_rejects_payload_hashes_that_are_not_lowercase_sha256(string? payloadHash)
    {
        Assert.Throws<ArgumentException>(() => AgentExecutionLedger.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "web",
            null,
            Guid.NewGuid(),
            "read_only_summarize_v1",
            payloadHash!));
    }

    [Fact]
    public void Lifecycle_rejects_a_different_actor_or_request()
    {
        var actorId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var ledger = CreateLedger(actorId, requestId);

        Assert.Throws<InvalidOperationException>(() => ledger.MarkVisionInFlight(Guid.NewGuid(), requestId));
        Assert.Throws<InvalidOperationException>(() => ledger.MarkVisionInFlight(actorId, Guid.NewGuid()));
    }

    private static AgentExecutionLedger CreateLedger(Guid actorId, Guid requestId) => AgentExecutionLedger.Create(
        Guid.NewGuid(),
        Guid.NewGuid(),
        actorId,
        "web",
        null,
        requestId,
        "read_only_summarize_v1",
        new string('a', 64));
}
