namespace TradingTerminal.Infrastructure.TsdBridge;

/// <summary>
/// Lineage captured when a paper/sandbox confirm dispatches an order. The fill mirror reports this
/// back with the real OMS order id; it does not invent execution economics.
/// </summary>
public sealed record TsdFillBinding(
    string OrderId,
    string Symbol,
    string Exchange,
    string Side,
    string SignalId,
    string DaxOrderId,
    string BookId,
    string IntentVersion,
    string DedupeKey);

/// <summary>
/// One committed fill. <paramref name="IncrementalQuantity"/> is the new slice.
/// <paramref name="CumulativeQuantity"/> is the order's filled total after that slice, when known.
/// </summary>
public readonly record struct TsdFillObservation(
    string OrderId,
    decimal IncrementalQuantity,
    decimal Price,
    string FillKey,
    decimal CumulativeQuantity);

/// <summary>Posts one fill mirror. Implementations must be safe to retry with the same dedupe key.</summary>
public interface ITsdFillPoster
{
    Task<bool> PostAsync(BridgeFillDto fill, CancellationToken cancellationToken);
}

/// <summary>Posts through <see cref="TsdBridgeClient"/>. A false result leaves the fill retryable.</summary>
public sealed class TsdBridgeFillPoster : ITsdFillPoster
{
    private readonly TsdBridgeClient _client;

    public TsdBridgeFillPoster(TsdBridgeClient client) =>
        _client = client ?? throw new ArgumentNullException(nameof(client));

    public Task<bool> PostAsync(BridgeFillDto fill, CancellationToken cancellationToken) =>
        _client.PostFillAsync(fill, cancellationToken);
}

/// <summary>
/// Paper-only TSD fill mirror. Confirm registers the OMS order id; each newly appended fill is
/// posted with its real price and incremental quantity. Replayed fill keys and cumulative totals
/// that are already mirrored do not post again. A failed post stays pending for <see cref="FlushAsync"/>.
/// </summary>
public sealed class TsdFillMirror
{
    private const decimal Epsilon = 0.0000001m;
    private readonly ITsdFillPoster _poster;
    private readonly object _gate = new();
    private readonly Dictionary<string, OrderMirror> _orders = new(StringComparer.Ordinal);

    public TsdFillMirror(ITsdFillPoster poster) =>
        _poster = poster ?? throw new ArgumentNullException(nameof(poster));

    /// <summary>Registers one paper order. Live registrations are ignored. Repeat registration is a no-op.</summary>
    public bool TryRegister(TsdFillBinding binding, bool paper)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (!paper || string.IsNullOrWhiteSpace(binding.OrderId))
            return false;

        lock (_gate)
        {
            if (_orders.ContainsKey(binding.OrderId))
                return true;
            _orders.Add(binding.OrderId, new OrderMirror(binding));
            return true;
        }
    }

    /// <summary>Posts the unmirrored slice of one fill. Returns false when nothing new was acknowledged.</summary>
    public async Task<bool> ObserveAsync(TsdFillObservation observation, CancellationToken cancellationToken)
    {
        if (!TryBegin(observation, out var orderId, out var slot))
            return false;
        return await PublishAsync(orderId, slot, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Retries fills whose previous post was not acknowledged. Safe to call on each bridge poll.</summary>
    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        List<(string OrderId, FillSlot Slot)> pending;
        lock (_gate)
        {
            pending = [];
            foreach (var pair in _orders)
            {
                foreach (var slot in pair.Value.Slots.Values)
                {
                    if (slot.Status != FillSlotStatus.Failed)
                        continue;
                    slot.Status = FillSlotStatus.InFlight;
                    pending.Add((pair.Key, slot));
                }
            }
        }

        foreach (var item in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await PublishAsync(item.OrderId, item.Slot, cancellationToken).ConfigureAwait(false);
        }
    }

    private bool TryBegin(TsdFillObservation observation, out string orderId, out FillSlot slot)
    {
        orderId = observation.OrderId ?? "";
        slot = null!;
        if (string.IsNullOrWhiteSpace(orderId) ||
            string.IsNullOrWhiteSpace(observation.FillKey) ||
            observation.Price <= 0m)
        {
            return false;
        }

        lock (_gate)
        {
            if (!_orders.TryGetValue(orderId, out var order))
                return false;
            if (order.Slots.TryGetValue(observation.FillKey, out var existing))
            {
                if (existing.Status != FillSlotStatus.Failed)
                    return false;
                existing.Status = FillSlotStatus.InFlight;
                slot = existing;
                return true;
            }

            var delta = order.UnmirroredDelta(observation);
            if (delta <= Epsilon)
            {
                order.Slots[observation.FillKey] = FillSlot.Covered(observation.FillKey);
                return false;
            }

            var created = FillSlot.Create(order.Binding, observation, delta);
            order.Slots[observation.FillKey] = created;
            order.Reserved += delta;
            slot = created;
            return true;
        }
    }

    private async Task<bool> PublishAsync(string orderId, FillSlot slot, CancellationToken cancellationToken)
    {
        bool acknowledged;
        try
        {
            acknowledged = await _poster.PostAsync(slot.Dto, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Mark(orderId, slot, acknowledged: false);
            throw;
        }
        catch (Exception)
        {
            acknowledged = false;
        }

        Mark(orderId, slot, acknowledged);
        return acknowledged;
    }

    private void Mark(string orderId, FillSlot slot, bool acknowledged)
    {
        lock (_gate)
        {
            if (!_orders.TryGetValue(orderId, out var order) ||
                !order.Slots.TryGetValue(slot.FillKey, out var current) ||
                !ReferenceEquals(current, slot))
            {
                return;
            }

            if (acknowledged)
            {
                slot.Status = FillSlotStatus.Acknowledged;
                order.Mirrored += slot.Delta;
                return;
            }

            slot.Status = FillSlotStatus.Failed;
        }
    }

    private enum FillSlotStatus : byte
    {
        InFlight = 0,
        Acknowledged = 1,
        Failed = 2,
        Covered = 3,
    }

    private sealed class OrderMirror
    {
        public OrderMirror(TsdFillBinding binding) => Binding = binding;

        public TsdFillBinding Binding { get; }

        public Dictionary<string, FillSlot> Slots { get; } = new(StringComparer.Ordinal);

        public decimal Reserved { get; set; }

        public decimal Mirrored { get; set; }

        public decimal UnmirroredDelta(TsdFillObservation observation)
        {
            if (observation.CumulativeQuantity > 0m)
            {
                var gap = observation.CumulativeQuantity - Reserved;
                if (gap <= Epsilon)
                    return 0m;
                if (observation.IncrementalQuantity > Epsilon &&
                    Math.Abs(observation.IncrementalQuantity - gap) <= Epsilon)
                {
                    return observation.IncrementalQuantity;
                }

                return gap;
            }

            return observation.IncrementalQuantity > Epsilon ? observation.IncrementalQuantity : 0m;
        }
    }

    private sealed class FillSlot
    {
        private FillSlot(string fillKey, decimal delta, BridgeFillDto dto, FillSlotStatus status)
        {
            FillKey = fillKey;
            Delta = delta;
            Dto = dto;
            Status = status;
        }

        public string FillKey { get; }

        public decimal Delta { get; }

        public BridgeFillDto Dto { get; }

        public FillSlotStatus Status { get; set; }

        public static FillSlot Covered(string fillKey) =>
            new(fillKey, 0m, new BridgeFillDto(), FillSlotStatus.Covered);

        public static FillSlot Create(TsdFillBinding binding, TsdFillObservation observation, decimal delta)
        {
            var basis = string.IsNullOrWhiteSpace(binding.DedupeKey) ? binding.OrderId : binding.DedupeKey;
            return new FillSlot(
                observation.FillKey,
                delta,
                new BridgeFillDto
                {
                    Symbol = binding.Symbol,
                    Exchange = binding.Exchange,
                    Side = binding.Side,
                    Quantity = (double)delta,
                    Price = (double)observation.Price,
                    SignalId = binding.SignalId,
                    ClientOrderId = binding.OrderId,
                    DaxOrderId = binding.DaxOrderId,
                    BookId = binding.BookId,
                    IntentVersion = binding.IntentVersion,
                    DedupeKey = $"{basis}:{observation.FillKey}",
                },
                FillSlotStatus.InFlight);
        }
    }
}
