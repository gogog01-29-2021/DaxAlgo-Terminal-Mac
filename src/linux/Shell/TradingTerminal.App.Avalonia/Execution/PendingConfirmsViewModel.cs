using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TradingTerminal.ExecutionUi;
using TradingTerminal.Infrastructure.TsdBridge;

namespace TradingTerminal.App.Avalonia.Execution;

/// <summary>
/// Binds silent sidecar pending confirms into the Execution Console.
/// Labels stay generic ("Confirm trade") — no TSD jargon in the UI.
/// Confirm drives DaxAlgo OMS via the console ticket (Model A).
/// </summary>
public sealed partial class PendingConfirmsViewModel : ObservableObject, IDisposable
{
    private readonly ITsdPendingConfirmStore _store;
    private readonly TsdBridgeClient _client;
    private readonly Func<TsdPendingConfirm, CancellationToken, Task<ExecutionConfirmOutcome>>? _confirmIntoOms;

    public ObservableCollection<TsdPendingConfirm> Items { get; } = new();

    [ObservableProperty]
    private bool _hasItems;

    [ObservableProperty]
    private string _statusLine = "";

    public PendingConfirmsViewModel(
        ITsdPendingConfirmStore store,
        TsdBridgeClient client,
        Func<TsdPendingConfirm, CancellationToken, Task<ExecutionConfirmOutcome>>? confirmIntoOms = null)
    {
        _store = store;
        _client = client;
        _confirmIntoOms = confirmIntoOms;
        _store.Changed += OnChanged;
        Refresh();
        _ = ProbeAsync();
    }

    private async Task ProbeAsync()
    {
        var up = await _client.IsReachableAsync(CancellationToken.None).ConfigureAwait(true);
        StatusLine = up ? "" : ""; // stay silent when down
        // Optional quiet hint only when there are items
    }

    private void OnChanged() =>
        global::Avalonia.Threading.Dispatcher.UIThread.Post(Refresh);

    private void Refresh()
    {
        Items.Clear();
        foreach (var item in _store.Snapshot())
            Items.Add(item);
        HasItems = Items.Count > 0;
        if (HasItems)
            StatusLine = $"{Items.Count} awaiting confirm";
        else
            StatusLine = "";
    }

    [RelayCommand]
    private async Task ConfirmAsync(TsdPendingConfirm? item)
    {
        if (item is null) return;

        var outcome = ExecutionConfirmOutcome.Failed;
        if (_confirmIntoOms is not null)
            outcome = await _confirmIntoOms(item, CancellationToken.None).ConfigureAwait(true);

        if (!outcome.Ok)
        {
            StatusLine = "Ticket ready — select book / Submit if needed";
            Refresh();
            return;
        }

        // Model A: mirror fill back to TSD outbox/ledger (SoftFail if TSD down).
        // ClientOrderId/BookId are the real ids the OMS dispatched with (not a fabricated
        // "confirm:{id}" string) so TSD-side PPMS/TOMS reconciliation can correlate this fill with
        // the order's actual lifecycle, not just with the confirm click (S06 field-map promotion).
        var fillPrice = item.MarkPrice is > 0 ? item.MarkPrice.Value : 0d;
        await _client.PostFillAsync(
                new BridgeFillDto
                {
                    Symbol = item.Symbol,
                    Exchange = item.Exchange,
                    Side = item.Side,
                    Quantity = item.Quantity,
                    Price = fillPrice,
                    SignalId = string.Equals(item.Kind, "signal", StringComparison.OrdinalIgnoreCase)
                        ? item.Id
                        : "",
                    ClientOrderId = string.IsNullOrEmpty(outcome.OrderId) ? $"confirm:{item.Id}" : outcome.OrderId,
                    DaxOrderId = $"dax:{item.Id}",
                    BookId = outcome.BookId,
                    IntentVersion = item.IntentVersion,
                },
                CancellationToken.None)
            .ConfigureAwait(true);

        if (string.Equals(item.Kind, "intent", StringComparison.OrdinalIgnoreCase))
            await _client.SetIntentStatusAsync(item.Id, "confirmed", CancellationToken.None)
                .ConfigureAwait(true);
        _store.Acknowledge(item.Id);
        StatusLine = "";
        Refresh();
    }

    [RelayCommand]
    private async Task DismissAsync(TsdPendingConfirm? item)
    {
        if (item is null) return;
        if (string.Equals(item.Kind, "intent", StringComparison.OrdinalIgnoreCase))
            await _client.SetIntentStatusAsync(item.Id, "rejected", CancellationToken.None)
                .ConfigureAwait(true);
        _store.Acknowledge(item.Id);
        Refresh();
    }

    public void Dispose() => _store.Changed -= OnChanged;
}
