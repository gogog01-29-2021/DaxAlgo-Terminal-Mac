using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Infrastructure.TsdBridge;

/// <summary>
/// Background poller. When TSD is up, pending signals/intents appear in
/// <see cref="ITsdPendingConfirmStore"/> as ordinary confirms — no separate "TSD window".
/// When TSD is down, SoftFail keeps the app fully usable on DaxAlgo-only OMS.
/// </summary>
internal sealed class TsdBridgeHostedService : BackgroundService
{
    private readonly TsdBridgeClient _client;
    private readonly TsdPendingConfirmStore _store;
    private readonly IOptionsMonitor<TsdBridgeOptions> _options;
    private readonly ILogger<TsdBridgeHostedService> _logger;
    private long _sinceMs;
    private bool _announcedUp;

    public TsdBridgeHostedService(
        TsdBridgeClient client,
        TsdPendingConfirmStore store,
        IOptionsMonitor<TsdBridgeOptions> options,
        ILogger<TsdBridgeHostedService> logger)
    {
        _client = client;
        _store = store;
        _options = options;
        _logger = logger;
        _sinceMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 60_000;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var opt = _options.CurrentValue;
            var delay = TimeSpan.FromSeconds(Math.Max(1, opt.PollIntervalSeconds));
            try
            {
                if (!opt.Enabled)
                {
                    await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                if (!await _client.IsReachableAsync(stoppingToken).ConfigureAwait(false))
                {
                    _announcedUp = false;
                    await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                if (!_announcedUp)
                {
                    _announcedUp = true;
                    _logger.LogInformation("Connected to strategy sidecar (silent).");
                }

                var signals = await _client.GetSignalsAsync(_sinceMs, stoppingToken)
                    .ConfigureAwait(false);
                foreach (var s in signals)
                {
                    if (s.CreatedAtMs > _sinceMs)
                        _sinceMs = s.CreatedAtMs;
                    _store.Upsert(new TsdPendingConfirm(
                        Id: string.IsNullOrEmpty(s.SignalId) ? Guid.NewGuid().ToString("n") : s.SignalId,
                        Kind: "signal",
                        Symbol: s.Symbol,
                        Exchange: s.Exchange,
                        Side: s.Side,
                        Quantity: s.Quantity,
                        Strength: s.Strength,
                        Note: string.IsNullOrWhiteSpace(s.Note) ? "Strategy signal" : s.Note,
                        CreatedAtMs: s.CreatedAtMs,
                        SignalKind: MapKind(s.Kind),
                        MarkPrice: s.MarkPrice,
                        BookId: s.BookId,
                        ExecutionOwner: s.ExecutionOwner,
                        IntentVersion: s.StrategyVersion));
                }

                var intents = await _client.GetPendingIntentsAsync(stoppingToken)
                    .ConfigureAwait(false);
                foreach (var intent in intents)
                {
                    var symbol = ReadPayloadString(intent, "symbol") ?? "";
                    var exchange = ReadPayloadString(intent, "exchange") ?? "ALPACA_PAPER";
                    var qty = ReadPayloadDouble(intent, "target_quantity") ?? 0;
                    var side = qty >= 0 ? "BUY" : "SELL";
                    _store.Upsert(new TsdPendingConfirm(
                        Id: intent.IntentId,
                        Kind: "intent",
                        Symbol: symbol,
                        Exchange: exchange,
                        Side: side,
                        Quantity: Math.Abs(qty),
                        Strength: Math.Min(1.0, Math.Abs(qty) / 100.0),
                        Note: "Confirm trade",
                        CreatedAtMs: intent.CreatedAtMs,
                        SignalKind: qty < 0
                            ? StrategySignalKind.Short
                            : qty == 0
                                ? StrategySignalKind.Flat
                                : StrategySignalKind.Long,
                        BookId: ReadPayloadString(intent, "book_id") ?? "",
                        ExecutionOwner: ReadPayloadString(intent, "execution_owner") ?? "",
                        IntentVersion: ReadPayloadString(intent, "intent_version") ?? "",
                        GrantId: ReadPayloadString(intent, "grant_id") ?? "",
                        DedupeKey: ReadPayloadString(intent, "dedupe_key") ?? ""));
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "TSD bridge poll loop");
            }

            try
            {
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static StrategySignalKind MapKind(string? kind) =>
        (kind ?? "").Trim().ToLowerInvariant() switch
        {
            "long" or "buy" => StrategySignalKind.Long,
            "short" or "sell" => StrategySignalKind.Short,
            "flat" => StrategySignalKind.Flat,
            _ => StrategySignalKind.Flat,
        };

    private static string? ReadPayloadString(BridgeIntentDto intent, string key)
    {
        if (intent.Payload is null || !intent.Payload.TryGetValue(key, out var el))
            return null;
        return el.ValueKind == System.Text.Json.JsonValueKind.String
            ? el.GetString()
            : el.ToString();
    }

    private static double? ReadPayloadDouble(BridgeIntentDto intent, string key)
    {
        if (intent.Payload is null || !intent.Payload.TryGetValue(key, out var el))
            return null;
        return el.ValueKind switch
        {
            System.Text.Json.JsonValueKind.Number => el.GetDouble(),
            System.Text.Json.JsonValueKind.String when double.TryParse(el.GetString(), out var d) => d,
            _ => null,
        };
    }
}
