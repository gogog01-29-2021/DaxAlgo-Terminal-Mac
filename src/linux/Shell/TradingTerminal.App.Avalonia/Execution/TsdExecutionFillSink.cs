using TradingTerminal.Execution.Oms;
using TradingTerminal.ExecutionUi;
using TradingTerminal.Infrastructure.TsdBridge;

namespace TradingTerminal.App.Avalonia.Execution;

/// <summary>
/// Forwards paper OMS fills into the TSD mirror. The post is not awaited on the OMS worker; a failed
/// post stays pending and the bridge poll retries it.
/// </summary>
public sealed class TsdExecutionFillSink : IExecutionFillSink
{
    private readonly TsdFillMirror _mirror;

    public TsdExecutionFillSink(TsdFillMirror mirror) =>
        _mirror = mirror ?? throw new ArgumentNullException(nameof(mirror));

    public void OnExecutionFill(ExecutionFillNotice notice)
    {
        if (notice.Mode != ExecutionMode.Paper || string.IsNullOrWhiteSpace(notice.OrderId))
            return;
        _ = _mirror.ObserveAsync(
            new TsdFillObservation(
                notice.OrderId,
                notice.IncrementalQuantity,
                notice.Price,
                notice.FillKey,
                notice.CumulativeQuantity),
            CancellationToken.None);
    }
}
