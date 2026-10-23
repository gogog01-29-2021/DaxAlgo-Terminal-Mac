using FluentAssertions;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Execution.Oms;
using TradingTerminal.ExecutionUi;
using TradingTerminal.Sandbox.Runtime;
using Xunit;

namespace TradingTerminal.App.Avalonia.Tests;

/// <summary>
/// Covers the confirm path that drives a TSD-originated pending signal/intent into DaxAlgo's
/// OMS (<see cref="ExecutionConsoleViewModel.ConfirmPendingIntoOmsAsync"/>) -- the one live
/// order path today that both carries real order flow (Execution Console, live-capable) and
/// had zero existing test coverage before this file.
/// </summary>
public sealed class ExecutionConsoleViewModelConfirmIntoOmsTests
{
    [Fact]
    public async Task ConfirmPendingIntoOmsAsync_returns_true_and_no_error_when_oms_accepts()
    {
        var client = new FakeExecutionClient(ExecutionCommandResult.Success("filled", "daxt-book-1-1"));
        using var vm = CreateWithRealBookSelected(client);

        var outcome = await vm.ConfirmPendingIntoOmsAsync("AAPL", "BUY", 5);

        outcome.Ok.Should().BeTrue();
        vm.ManualTicketError.Should().BeEmpty();
        client.SubmitCalls.Should().Be(1);
    }

    [Fact]
    public async Task ConfirmPendingIntoOmsAsync_carries_the_real_order_id_and_book_id_for_fill_mirroring()
    {
        // PendingConfirmsViewModel.ConfirmAsync uses these to mirror a fill back to the TSD
        // bridge ledger with real lineage instead of a fabricated "confirm:{id}" correlation id.
        var client = new FakeExecutionClient(ExecutionCommandResult.Success("filled", "daxt-book-1-7"));
        using var vm = CreateWithRealBookSelected(client);

        var outcome = await vm.ConfirmPendingIntoOmsAsync("AAPL", "BUY", 5);

        outcome.OrderId.Should().Be("daxt-book-1-7");
        outcome.BookId.Should().Be("book-1");
        outcome.Paper.Should().BeTrue();
    }

    [Fact]
    public async Task ConfirmPendingIntoOmsAsync_returns_false_when_oms_rejects()
    {
        // This is the safety property that matters: if DaxAlgo's own OMS/risk engine
        // rejects the order, the caller (PendingConfirmsViewModel.ConfirmAsync) must NOT
        // go on to mirror a phantom fill back to TSD's ledger.
        var client = new FakeExecutionClient(
            ExecutionCommandResult.Failure("Risk engine rejected: gross exposure limit exceeded"));
        using var vm = CreateWithRealBookSelected(client);

        var outcome = await vm.ConfirmPendingIntoOmsAsync("AAPL", "BUY", 5);

        outcome.Ok.Should().BeFalse(
            "a risk-rejected order must not be reported to the caller as confirmed -- " +
            "the caller mirrors a fill to TSD and marks the intent 'confirmed' whenever this returns true");
        client.SubmitCalls.Should().Be(1, "the attempt should still reach the OMS -- this checks the OMS outcome is honored, not that dispatch was skipped");
    }

    [Fact]
    public async Task ConfirmPendingIntoOmsAsync_surfaces_the_oms_rejection_reason()
    {
        var client = new FakeExecutionClient(
            ExecutionCommandResult.Failure("Risk engine rejected: gross exposure limit exceeded"));
        using var vm = CreateWithRealBookSelected(client);

        await vm.ConfirmPendingIntoOmsAsync("AAPL", "BUY", 5);

        vm.ManualTicketError.Should().Contain("Risk engine rejected");
    }

    [Fact]
    public async Task ConfirmPendingIntoOmsAsync_returns_false_when_no_book_selected()
    {
        var client = new FakeExecutionClient(ExecutionCommandResult.Success("filled"), includeBook: false);
        using var vm = new ExecutionConsoleViewModel(
            client, new DeniedExecutionConfirmationService(), new FakeBrokerLoginFormFactory());

        var outcome = await vm.ConfirmPendingIntoOmsAsync("AAPL", "BUY", 5);

        outcome.Ok.Should().BeFalse();
        client.SubmitCalls.Should().Be(0, "with no book/instrument match, dispatch must never reach the OMS");
    }

    /// <summary>
    /// RefreshNow() defaults SelectedBookEntry to the synthetic "All books" aggregate entry
    /// (Id "all"), whose Book is null -- so every test that needs a real single-book ticket
    /// must select the real book explicitly first.
    /// </summary>
    private static ExecutionConsoleViewModel CreateWithRealBookSelected(FakeExecutionClient client)
    {
        var vm = new ExecutionConsoleViewModel(
            client, new DeniedExecutionConfirmationService(), new FakeBrokerLoginFormFactory());
        vm.SelectedBookEntry = vm.BookEntries.First(entry => !entry.IsAllBooks);
        return vm;
    }

    private sealed class FakeBrokerLoginFormFactory : IBrokerLoginFormFactory
    {
        public IReadOnlyList<IBrokerLoginForm> All => Array.Empty<IBrokerLoginForm>();
        public IBrokerLoginForm Get(BrokerKind kind) => throw new NotSupportedException();
    }

    private sealed class FakeExecutionClient : IExecutionClient
    {
        private readonly ExecutionCommandResult _submitResult;
        private readonly ExecutionBookReadModel[] _books;

        public FakeExecutionClient(ExecutionCommandResult submitResult, bool includeBook = true)
        {
            _submitResult = submitResult;
            _books = includeBook ? new[] { MinimalBookWithInstrument() } : Array.Empty<ExecutionBookReadModel>();
        }

        public int SubmitCalls { get; private set; }

        public event EventHandler? SnapshotInvalidated
        {
            add { }
            remove { }
        }

        public ExecutionConsoleSnapshot GetSnapshot() =>
            new(
                Array.Empty<ExecutionAdapterReadModel>(),
                _books,
                MinimalAnalytics(),
                DateTime.UtcNow,
                null);

        private static ExecutionPortfolioAnalyticsReadModel MinimalAnalytics() =>
            new(
                Periods: Enum.GetValues<ExecutionTimeRange>()
                    .Select(range => new ExecutionPeriodAnalyticsReadModel(
                        range,
                        range.ToString(),
                        new ExecutionMetricResult(0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
                        Array.Empty<ExecutionEquityPointReadModel>(),
                        Array.Empty<ExecutionDailyPnlPointReadModel>()))
                    .ToArray(),
                ExposureByBook: Array.Empty<ExecutionExposureReadModel>(),
                ExecutionQuality: new ExecutionQualityReadModel(0, 0, 0, 0, 0, 0, 0, 0d, 0, 0d));

        public ValueTask<ExecutionCommandResult> SetIntakePausedAsync(string bookId, bool paused, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ExecutionCommandResult.Success("ok"));

        public ValueTask<ExecutionCommandResult> ReconcileAsync(string bookId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ExecutionCommandResult.Success("ok"));

        public ValueTask<ExecutionCommandResult> KillAsync(string bookId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ExecutionCommandResult.Success("ok"));

        public ValueTask<ExecutionCommandResult> SetExecutionModeAsync(ExecutionModeChangeRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ExecutionCommandResult.Success("ok"));

        public ValueTask<ExecutionCommandResult> ConnectAdapterAsync(string adapterId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ExecutionCommandResult.Success("ok"));

        public ValueTask<ExecutionCommandResult> ConnectAdapterAsync(ExecutionAdapterConnectRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ExecutionCommandResult.Success("ok"));

        public ValueTask<ExecutionCommandResult> DisconnectAdapterAsync(string adapterId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ExecutionCommandResult.Success("ok"));

        public ValueTask<ExecutionCommandResult> CreateBookAsync(ExecutionBookCreateRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ExecutionCommandResult.Success("ok"));

        public ValueTask<ExecutionCommandResult> SubmitManualOrderAsync(ExecutionManualOrderRequest request, CancellationToken cancellationToken = default)
        {
            SubmitCalls++;
            return ValueTask.FromResult(_submitResult);
        }

        public ValueTask<ExecutionTargetSubmissionResult> SubmitTargetAsync(
            string bookId,
            TradingTerminal.Execution.TradeIntent intent,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ExecutionTargetSubmissionResult.Success("forwarded"));

        public void Dispose()
        {
        }

        private static ExecutionBookReadModel MinimalBookWithInstrument() =>
            new(
                "book-1",
                "Test book",
                "paper",
                "paper",
                Array.Empty<string>(),
                "$0",
                ExecutionTone.Neutral,
                new ExecutionLeaseReadModel(ExecutionLeaseStatus.Held, 1, "ok"),
                IsIntakePaused: false,
                AdmissionOpen: true,
                OpenRealPositionCount: 0,
                Positions: Array.Empty<ExecutionPositionReadModel>(),
                Orders: Array.Empty<ExecutionOrderReadModel>(),
                History: Array.Empty<ExecutionHistoryReadModel>(),
                ReconciliationCases: Array.Empty<ExecutionReconciliationReadModel>(),
                Risk: new ExecutionRiskReadModel([], "ok"),
                LedgerEvents: Array.Empty<ExecutionLedgerEventReadModel>(),
                Analytics: MinimalAnalytics(),
                Mode: ExecutionMode.Paper)
            {
                TradableInstruments = new[] { new ExecutionTradableInstrumentReadModel(new InstrumentId(1), "AAPL") },
            };
    }
}
