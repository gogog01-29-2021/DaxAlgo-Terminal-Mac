using TradingTerminal.Execution.Oms;

namespace TradingTerminal.ExecutionUi;

/// <summary>One paper fill committed by a book runtime, in exact decimal units.</summary>
public readonly record struct ExecutionFillNotice(
    string OrderId,
    decimal IncrementalQuantity,
    decimal Price,
    string FillKey,
    decimal CumulativeQuantity,
    ExecutionMode Mode);

/// <summary>Receives paper fills from the in-process console. Live fills are not delivered.</summary>
public interface IExecutionFillSink
{
    void OnExecutionFill(ExecutionFillNotice notice);
}

/// <summary>
/// Replays fills that committed before a TSD registration existed. Simulated submits fill inside
/// the dispatch call, so the confirm path registers and then replays.
/// </summary>
public interface IExecutionFillReplay
{
    void ReplayCommittedFills(string orderId);
}
