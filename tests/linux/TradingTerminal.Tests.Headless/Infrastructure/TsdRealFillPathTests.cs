using FluentAssertions;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Time;
using TradingTerminal.Execution;
using TradingTerminal.Execution.Alpaca;
using TradingTerminal.Execution.Oms;
using TradingTerminal.ExecutionUi;
using TradingTerminal.Infrastructure.TsdBridge;
using Xunit;

namespace TradingTerminal.Tests.Headless.Infrastructure;

/// <summary>
/// Paper fill mirrors follow committed OMS fills. Alpaca updates are fakes; no credentials are read.
/// </summary>
public sealed class TsdRealFillPathTests
{
    private const int InstrumentId = 7101;
    private const string Symbol = "AAPL";
    private static readonly DateTime NowUtc = new(2026, 3, 5, 15, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Alpaca_full_fill_mirrors_the_trade_update_price_and_quantity()
    {
        await using var harness = CreateAlpaca();
        var orderId = await harness.SubmitLimitAsync(4, new ScaledPrice(19_125, 2));
        harness.Register(orderId);

        harness.Push(harness.Snapshot(orderId, "filled", 4, new ScaledPrice(19_125, 2), NowUtc.AddSeconds(2)));
        harness.Scheduler.RunAll();
        await harness.DrainAsync();

        harness.Poster.Posts.Should().ContainSingle();
        var fill = harness.Poster.Posts[0];
        fill.ClientOrderId.Should().Be(orderId);
        fill.Quantity.Should().Be(4d);
        fill.Price.Should().Be(191.25d);
        fill.BookId.Should().Be(harness.BookId);
        fill.DedupeKey.Should().StartWith("dedupe-1:");
        fill.Side.Should().Be("BUY");
        fill.Symbol.Should().Be(Symbol);
    }

    [Fact]
    public async Task Alpaca_partial_replay_and_reconnect_poll_do_not_double_count()
    {
        await using var harness = CreateAlpaca();
        var orderId = await harness.SubmitLimitAsync(5, new ScaledPrice(19_000, 2));
        harness.Register(orderId);

        var partial = harness.Snapshot(orderId, "partially_filled", 2, new ScaledPrice(19_000, 2), NowUtc.AddSeconds(2));
        harness.Push(partial);
        harness.Scheduler.RunAll();
        await harness.DrainAsync();

        harness.Push(partial);
        harness.Scheduler.RunAll();
        await harness.DrainAsync();
        harness.Poster.Posts.Should().ContainSingle();
        harness.Poster.Posts[0].Quantity.Should().Be(2d);
        harness.Poster.Posts[0].Price.Should().Be(190d);

        harness.Transport.Upsert(harness.Snapshot(
            orderId, "filled", 5, new ScaledPrice(19_000, 2), NowUtc.AddSeconds(5)));
        harness.Transport.ReportOrders = true;
        var reconciled = await harness.Client.ReconcileAsync(harness.BookId);
        reconciled.IsSuccess.Should().BeTrue(reconciled.Message);
        harness.Scheduler.RunAll();
        await harness.DrainAsync();

        harness.Poster.Posts.Should().HaveCount(2);
        harness.Poster.Posts[1].Quantity.Should().Be(3d);
        harness.Poster.Posts[1].Price.Should().Be(190d);
        harness.Poster.Posts[1].ClientOrderId.Should().Be(orderId);
        harness.Poster.Posts[1].DedupeKey.Should().NotBe(harness.Poster.Posts[0].DedupeKey);

        var again = await harness.Client.ReconcileAsync(harness.BookId);
        again.IsSuccess.Should().BeTrue(again.Message);
        harness.Scheduler.RunAll();
        await harness.DrainAsync();
        harness.Poster.Posts.Should().HaveCount(2);
    }

    [Fact]
    public async Task Simulated_fill_is_replayed_once_from_the_committed_event()
    {
        var poster = new RecordingPoster();
        var mirror = new TsdFillMirror(poster);
        var sink = new RecordingSink(mirror);
        using var client = new InProcessExecutionClient(fillSink: sink);

        var created = await client.CreateBookAsync(new ExecutionBookCreateRequest(
            "Paper book",
            "paper",
            ["alpha"],
            new InstrumentId(42),
            "AAPL"));
        created.IsSuccess.Should().BeTrue(created.Message);
        var book = client.GetSnapshot().Books.Single();

        var submitted = await client.SubmitTargetAsync(
            book.Id,
            new TradeIntent(
                new InstrumentId(42),
                TradeIntentQuantityMode.TargetPosition,
                ScaledQuantity.FromWhole(2),
                null,
                null,
                ScaledMoney.Zero,
                "alpha",
                1,
                "1"));
        submitted.IsSuccess.Should().BeTrue(submitted.Message);
        await sink.DrainAsync();
        poster.Posts.Should().BeEmpty();

        var orderId = client.GetSnapshot().Books.Single().Orders.Should().ContainSingle().Which.ClientOrderId;
        mirror.TryRegister(
            new TsdFillBinding(orderId, "AAPL", "SIM", "BUY", "sig", "dax:sig", book.Id, "sv", "dedupe"),
            paper: true);
        client.ReplayCommittedFills(orderId);
        await sink.DrainAsync();

        poster.Posts.Should().ContainSingle();
        poster.Posts[0].Quantity.Should().Be(2d);
        poster.Posts[0].Price.Should().Be(100d);
        poster.Posts[0].ClientOrderId.Should().Be(orderId);
        poster.Posts[0].DedupeKey.Should().StartWith("dedupe:");

        client.ReplayCommittedFills(orderId);
        await sink.DrainAsync();
        poster.Posts.Should().ContainSingle();
    }

    private static AlpacaHarness CreateAlpaca() => new();

    private sealed class AlpacaHarness : IAsyncDisposable
    {
        public AlpacaHarness()
        {
            Poster = new RecordingPoster();
            Mirror = new TsdFillMirror(Poster);
            Sink = new RecordingSink(Mirror);
            var accountId = $"PA-FILL-{Guid.NewGuid():N}";
            var options = new AlpacaExecutionOptions
            {
                Enabled = true,
                Mode = ExecutionMode.Paper,
                Symbol = Symbol,
                CanonicalInstrumentId = InstrumentId,
                KeyId = "test-key",
                SecretKey = "test-secret",
                ExpectedAccountId = accountId,
            };
            Transport = new FakeAlpacaTransport(AlpacaExecutionEndpointGate.Resolve(options), accountId);
            Updates = new PushTradeUpdateSource();
            Scheduler = new ControllableAdapterEventScheduler();
            Adapter = new AlpacaExecutionAdapter(options, Transport, Updates, new FixedClock(NowUtc), Scheduler);
            Client = new InProcessExecutionClient(
                registeredAdapters: [Adapter],
                liveConfirmationStore: new InMemoryLiveExecutionConfirmationStore(),
                alpacaOptions: options,
                executionClock: new FixedClock(NowUtc),
                executionLeaseStore: new InMemoryExecutionLeaseStore(),
                fillSink: Sink);
        }

        public RecordingPoster Poster { get; }

        public TsdFillMirror Mirror { get; }

        public RecordingSink Sink { get; }

        public FakeAlpacaTransport Transport { get; }

        public PushTradeUpdateSource Updates { get; }

        public ControllableAdapterEventScheduler Scheduler { get; }

        public AlpacaExecutionAdapter Adapter { get; }

        public InProcessExecutionClient Client { get; }

        public string BookId { get; private set; } = "";

        public async Task<string> SubmitLimitAsync(long quantity, ScaledPrice limit)
        {
            var connected = await Client.ConnectAdapterAsync(new ExecutionAdapterConnectRequest(
                AlpacaExecutionAdapter.StableAdapterId,
                KeyId: "test-key",
                SecretKey: "test-secret"));
            connected.IsSuccess.Should().BeTrue(connected.Message);
            Scheduler.RunAll();

            var created = await Client.CreateBookAsync(new ExecutionBookCreateRequest(
                "Alpaca paper",
                AlpacaExecutionAdapter.StableAdapterId,
                Array.Empty<string>()));
            created.IsSuccess.Should().BeTrue(created.Message);
            var book = Client.GetSnapshot().Books.Single();
            BookId = book.Id;
            book.Mode.Should().Be(ExecutionMode.Paper);

            var submitted = await Client.SubmitManualOrderAsync(new ExecutionManualOrderRequest(
                book.Id,
                book.TradableInstruments[0].Instrument,
                Symbol,
                ExecutionManualOrderSide.Buy,
                ScaledQuantity.FromWhole(quantity),
                ExecutionManualOrderType.Limit,
                limit));
            submitted.IsSuccess.Should().BeTrue(submitted.Message);
            submitted.OrderId.Should().NotBeNullOrWhiteSpace();
            Scheduler.RunAll();
            await Sink.DrainAsync();
            Poster.Posts.Should().BeEmpty();
            return submitted.OrderId;
        }

        public void Register(string orderId) =>
            Mirror.TryRegister(
                new TsdFillBinding(orderId, Symbol, "ALPACA_PAPER", "BUY", "sig-1", "dax:sig-1", BookId, "sv-1", "dedupe-1"),
                paper: true).Should().BeTrue();

        public void Push(AlpacaOrderSnapshot snapshot)
        {
            Transport.Upsert(snapshot);
            Updates.Push(snapshot);
        }

        public AlpacaOrderSnapshot Snapshot(
            string clientOrderId,
            string status,
            long filled,
            ScaledPrice average,
            DateTime updatedAtUtc)
        {
            var brokerId = Transport.BrokerIdFor(clientOrderId);
            return new AlpacaOrderSnapshot(
                brokerId,
                clientOrderId,
                Symbol,
                "us_equity",
                "buy",
                "limit",
                "day",
                status,
                ScaledQuantity.FromWhole(5),
                ScaledQuantity.FromWhole(filled),
                average,
                average,
                null,
                updatedAtUtc);
        }

        public Task DrainAsync() => Sink.DrainAsync();

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Adapter.DisposeAsync();
        }
    }

    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }

    private sealed class RecordingPoster : ITsdFillPoster
    {
        public List<BridgeFillDto> Posts { get; } = [];

        public Task<bool> PostAsync(BridgeFillDto fill, CancellationToken cancellationToken)
        {
            Posts.Add(fill);
            return Task.FromResult(true);
        }
    }

    private sealed class RecordingSink : IExecutionFillSink
    {
        private readonly TsdFillMirror _mirror;
        private readonly List<Task> _pending = [];

        public RecordingSink(TsdFillMirror mirror) => _mirror = mirror;

        public void OnExecutionFill(ExecutionFillNotice notice)
        {
            _pending.Add(_mirror.ObserveAsync(
                new TsdFillObservation(
                    notice.OrderId,
                    notice.IncrementalQuantity,
                    notice.Price,
                    notice.FillKey,
                    notice.CumulativeQuantity),
                CancellationToken.None));
        }

        public async Task DrainAsync()
        {
            var batch = _pending.ToArray();
            _pending.Clear();
            await Task.WhenAll(batch);
        }
    }

    private sealed class PushTradeUpdateSource : IAlpacaTradeUpdateSource
    {
        public bool IsRunning { get; private set; }

        public event Action<AlpacaOrderSnapshot>? OrderUpdated;

        public event Action<Exception>? Faulted
        {
            add { }
            remove { }
        }

        public Task StartAsync(IAlpacaExecutionTransport transport, CancellationToken cancellationToken = default)
        {
            IsRunning = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            IsRunning = false;
            return Task.CompletedTask;
        }

        public void Push(AlpacaOrderSnapshot snapshot) => OrderUpdated?.Invoke(snapshot);

        public ValueTask DisposeAsync()
        {
            IsRunning = false;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeAlpacaTransport(AlpacaExecutionEndpoint endpoint, string accountId) : IAlpacaExecutionTransport
    {
        private readonly Dictionary<string, AlpacaOrderSnapshot> _orders = new(StringComparer.Ordinal);

        public bool ReportOrders { get; set; }

        public AlpacaExecutionEndpoint Endpoint { get; } = endpoint;

        public bool IsConnected { get; private set; }

        public Task ConnectAsync(string keyId, string secretKey, CancellationToken cancellationToken = default)
        {
            IsConnected = true;
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            IsConnected = false;
            return Task.CompletedTask;
        }

        public Task<AlpacaAccountSnapshot> GetAccountAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AlpacaAccountSnapshot(
                accountId,
                "ACTIVE",
                "USD",
                new ScaledMoney(100_000_00, 2),
                new ScaledMoney(200_000_00, 2),
                TradingBlocked: false,
                AccountBlocked: false));

        public Task<AlpacaAssetSnapshot> GetAssetAsync(string symbol, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AlpacaAssetSnapshot(
                symbol,
                "us_equity",
                Tradable: true,
                Fractionable: false,
                MinimumOrderSize: null,
                MinimumTradeIncrement: null,
                PriceIncrement: new ScaledPrice(1, 2)));

        public Task<AlpacaLatestTrade?> GetLatestTradeAsync(string symbol, CancellationToken cancellationToken = default) =>
            Task.FromResult<AlpacaLatestTrade?>(new AlpacaLatestTrade(new ScaledPrice(19_000, 2), NowUtc));

        public Task<AlpacaOrderSnapshot> SubmitOrderAsync(
            AlpacaSubmitRequest request,
            CancellationToken cancellationToken = default)
        {
            var accepted = new AlpacaOrderSnapshot(
                $"broker-{_orders.Count + 1}",
                request.ClientOrderId,
                request.Symbol,
                "us_equity",
                request.Side,
                request.OrderType,
                request.TimeInForce,
                "new",
                request.Quantity,
                ScaledQuantity.Zero,
                null,
                request.LimitPrice,
                request.StopPrice,
                NowUtc);
            Upsert(accepted);
            return Task.FromResult(accepted);
        }

        public Task CancelOrderAsync(string orderId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<AlpacaOrderSnapshot> ReplaceOrderAsync(
            string orderId,
            AlpacaReplaceRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_orders.Values.Single(order => order.OrderId == orderId));

        public Task<AlpacaOrderSnapshot?> GetOrderByIdAsync(string orderId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_orders.Values.FirstOrDefault(order => order.OrderId == orderId));

        public Task<AlpacaOrderSnapshot?> GetOrderByClientIdAsync(
            string clientOrderId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_orders.TryGetValue(clientOrderId, out var order) ? order : null);

        public Task<IReadOnlyList<AlpacaOrderSnapshot>> GetOrdersAsync(
            AlpacaOrderStatusFilter status,
            CancellationToken cancellationToken = default)
        {
            if (!ReportOrders)
                return Task.FromResult<IReadOnlyList<AlpacaOrderSnapshot>>([]);
            var wantOpen = status == AlpacaOrderStatusFilter.Open;
            IReadOnlyList<AlpacaOrderSnapshot> rows = _orders.Values
                .Where(order => IsOpen(order.Status) == wantOpen || status == AlpacaOrderStatusFilter.All)
                .ToArray();
            return Task.FromResult(rows);
        }

        public Task<IReadOnlyList<AlpacaPositionSnapshot>> GetPositionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AlpacaPositionSnapshot>>([]);

        public void Upsert(AlpacaOrderSnapshot snapshot) => _orders[snapshot.ClientOrderId] = snapshot;

        public string BrokerIdFor(string clientOrderId) => _orders[clientOrderId].OrderId;

        public ValueTask DisposeAsync()
        {
            IsConnected = false;
            return ValueTask.CompletedTask;
        }

        private static bool IsOpen(string status) => status.ToLowerInvariant() is
            "new" or "accepted" or "pending_new" or "accepted_for_bidding" or
            "partially_filled" or "pending_cancel" or "pending_replace";
    }
}
