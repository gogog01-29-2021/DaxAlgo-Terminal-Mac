using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.ExecutionUi;
using TradingTerminal.Infrastructure.TsdBridge;

namespace TradingTerminal.App.Avalonia.Execution;

/// <summary>
/// Thin Avalonia view over the shared <c>ExecutionConsoleViewModel</c>: Brokers, New book (Paper or
/// Real), and the manual ticket. Every gate lives in the view-model and the OMS behind it.
/// Pending sidecar confirms attach silently under the header when present.
/// </summary>
public partial class ExecutionConsoleWindow : Window
{
    private PendingConfirmsViewModel? _pending;

    public ExecutionConsoleWindow() => InitializeComponent();

    /// <summary>Wire silent confirm strip; safe no-op if sidecar services are absent.</summary>
    public void AttachSidecarConfirms(IServiceProvider services)
    {
        var store = services.GetService<ITsdPendingConfirmStore>();
        var client = services.GetService<TsdBridgeClient>();
        if (store is null || client is null)
            return;

        _pending = new PendingConfirmsViewModel(store, client, ConfirmIntoOmsAsync);
        if (this.FindControl<Border>("PendingConfirmsHost") is { } host)
        {
            host.DataContext = _pending;
            void SyncVisibility() => host.IsVisible = _pending.HasItems;
            _pending.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(PendingConfirmsViewModel.HasItems) or null)
                    global::Avalonia.Threading.Dispatcher.UIThread.Post(SyncVisibility);
            };
            SyncVisibility();
        }
    }

    private async Task<ExecutionConfirmOutcome> ConfirmIntoOmsAsync(TsdPendingConfirm item, CancellationToken ct)
    {
        if (DataContext is not ExecutionConsoleViewModel console)
            return ExecutionConfirmOutcome.Failed;
        return await console.ConfirmPendingIntoOmsAsync(
            item.Symbol,
            item.Side,
            item.Quantity,
            ct).ConfigureAwait(true);
    }

    protected override void OnClosed(EventArgs e)
    {
        _pending?.Dispose();
        _pending = null;
        base.OnClosed(e);
    }
}
