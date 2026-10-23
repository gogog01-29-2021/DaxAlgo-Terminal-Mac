using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Infrastructure.TsdBridge;

/// <summary>
/// One pending confirm surfaced to the UI as a normal strategy signal, not a "TSD" banner.
/// BookId/ExecutionOwner/IntentVersion/GrantId/DedupeKey are the S06 field-map promotion (host
/// admit lineage) carried through from the bridge payload so the eventual fill mirror can report
/// real lineage instead of inventing a correlation id.
/// </summary>
public sealed record TsdPendingConfirm(
    string Id,
    string Kind, // signal | intent
    string Symbol,
    string Exchange,
    string Side,
    double Quantity,
    double Strength,
    string Note,
    long CreatedAtMs,
    StrategySignalKind SignalKind,
    double? MarkPrice = null,
    string BookId = "",
    string ExecutionOwner = "",
    string IntentVersion = "",
    string GrantId = "",
    string DedupeKey = "");


/// <summary>In-memory inbox the Execution Console / shell can bind without knowing about HTTP.</summary>
public interface ITsdPendingConfirmStore
{
    IReadOnlyList<TsdPendingConfirm> Snapshot();
    event Action? Changed;
    void Acknowledge(string id);
}

internal sealed class TsdPendingConfirmStore : ITsdPendingConfirmStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, TsdPendingConfirm> _items = new(StringComparer.Ordinal);

    public event Action? Changed;

    public IReadOnlyList<TsdPendingConfirm> Snapshot()
    {
        lock (_gate)
            return _items.Values.OrderByDescending(x => x.CreatedAtMs).ToList();
    }

    public void Upsert(TsdPendingConfirm item)
    {
        lock (_gate)
            _items[item.Id] = item;
        Changed?.Invoke();
    }

    public void Acknowledge(string id)
    {
        lock (_gate)
            _items.Remove(id);
        Changed?.Invoke();
    }
}

/// <summary>HTTP client for TSD /api/bridge — silent when SoftFail.</summary>
public sealed class TsdBridgeClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly IOptionsMonitor<TsdBridgeOptions> _options;
    private readonly ILogger<TsdBridgeClient> _logger;

    public TsdBridgeClient(
        HttpClient http,
        IOptionsMonitor<TsdBridgeOptions> options,
        ILogger<TsdBridgeClient> logger)
    {
        _http = http;
        _options = options;
        _logger = logger;
    }

    public bool IsConfigured => _options.CurrentValue.Enabled
        && !string.IsNullOrWhiteSpace(_options.CurrentValue.BaseUrl);

    public async Task<bool> IsReachableAsync(CancellationToken ct)
    {
        if (!IsConfigured) return false;
        try
        {
            using var resp = await _http.GetAsync("api/bridge/status", ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            if (!_options.CurrentValue.SoftFail)
                _logger.LogWarning(ex, "TSD bridge unreachable");
            else
                _logger.LogDebug(ex, "TSD bridge unreachable (soft)");
            return false;
        }
    }

    public async Task<IReadOnlyList<BridgeSignalDto>> GetSignalsAsync(long sinceMs, CancellationToken ct)
    {
        try
        {
            var url = $"api/bridge/signals?since_ms={sinceMs}&limit=50";
            var body = await _http.GetFromJsonAsync<SignalsResponse>(url, JsonOptions, ct)
                .ConfigureAwait(false);
            return (IReadOnlyList<BridgeSignalDto>)(body?.Signals ?? new List<BridgeSignalDto>());
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "TSD signals poll failed");
            return Array.Empty<BridgeSignalDto>();
        }
    }

    public async Task<IReadOnlyList<BridgeIntentDto>> GetPendingIntentsAsync(CancellationToken ct)
    {
        try
        {
            var body = await _http.GetFromJsonAsync<IntentsResponse>(
                    "api/bridge/intents?status=pending", JsonOptions, ct)
                .ConfigureAwait(false);
            return (IReadOnlyList<BridgeIntentDto>)(body?.Intents ?? new List<BridgeIntentDto>());
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "TSD intents poll failed");
            return Array.Empty<BridgeIntentDto>();
        }
    }

    /// <summary>Returns false when the mirror was not acknowledged. Callers may retry the same dedupe key.</summary>
    public async Task<bool> PostFillAsync(BridgeFillDto fill, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.PostAsJsonAsync("api/bridge/fills", fill, JsonOptions, ct)
                .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogDebug("TSD fill mirror returned {StatusCode}", (int)resp.StatusCode);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "TSD fill mirror failed");
            return false;
        }
    }

    public async Task SetIntentStatusAsync(string intentId, string status, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.PostAsJsonAsync(
                    $"api/bridge/intents/{Uri.EscapeDataString(intentId)}/status",
                    new { status },
                    JsonOptions,
                    ct)
                .ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "TSD intent status update failed");
        }
    }
}

public sealed class BridgeSignalDto
{
    public string SignalId { get; set; } = "";
    public string Kind { get; set; } = "";
    public double Strength { get; set; }
    public string Symbol { get; set; } = "";
    public string Exchange { get; set; } = "";
    public double Quantity { get; set; }
    public string Side { get; set; } = "";
    public string Note { get; set; } = "";
    public long CreatedAtMs { get; set; }
    public double? MarkPrice { get; set; }
    public string BookId { get; set; } = "";
    public string ExecutionOwner { get; set; } = "";
    public string StrategyVersion { get; set; } = "";
}

public sealed class BridgeIntentDto
{
    public string IntentId { get; set; } = "";
    public string Status { get; set; } = "";
    public long CreatedAtMs { get; set; }
    public Dictionary<string, JsonElement>? Payload { get; set; }
}

public sealed class BridgeFillDto
{
    public string Symbol { get; set; } = "";
    public string Exchange { get; set; } = "";
    public string Side { get; set; } = "";
    public double Quantity { get; set; }
    public double Price { get; set; }
    public string SignalId { get; set; } = "";
    public string ClientOrderId { get; set; } = "";
    public string DaxOrderId { get; set; } = "";
    public string BookId { get; set; } = "";
    public string IntentVersion { get; set; } = "";
    public string DedupeKey { get; set; } = "";
}

internal sealed class SignalsResponse
{
    public List<BridgeSignalDto>? Signals { get; set; }
}

internal sealed class IntentsResponse
{
    public List<BridgeIntentDto>? Intents { get; set; }
}
