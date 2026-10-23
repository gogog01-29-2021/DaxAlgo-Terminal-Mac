namespace TradingTerminal.Execution.Oms;

/// <summary>
/// Notified after a fill is newly appended to the OMS ledger. Exact inbox replay does not notify,
/// so a subscriber cannot double-count a duplicate venue callback.
/// </summary>
public interface IExecutionFillObserver
{
    /// <summary>Observes one newly committed fill. Implementations must not throw.</summary>
    void OnFillAppended(
        ClientOrderId orderId,
        FillExecution fill,
        DeduplicationKey fillKey,
        ScaledQuantity cumulativeFilled);
}
