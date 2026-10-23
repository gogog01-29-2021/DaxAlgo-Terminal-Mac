# TSD ↔ DaxAlgo Mac bridge (canonical pointer)

**Authority:** `/Users/w/Developer/tsd/.omx/plans/bridge-contract.md`

Mac implementation notes below. Naming: **Model A** only (never “Option A”).

User-facing: **DaxAlgo only**. Confirm trades / Execution Console / Alpaca Connect as usual.

Under the hood (invisible):

1. TSD API on `http://127.0.0.1:8000` (`TSD_EXECUTION_MODE=signal`)
2. Appsettings `TsdBridge` (Enabled, SoftFail) → `AddTsdBridge`
3. `TsdBridgeHostedService` polls `/api/bridge/signals|intents`
4. Pending items land in `ITsdPendingConfirmStore` as ordinary confirms
5. **Confirm** → prefills Execution Console manual ticket and submits when the selected book can take a market order
6. After a **committed OMS fill** (not the confirm click) → `TsdFillMirror` posts `BridgeFillDto` with the real fill price, the incremental quantity, the OMS order id, and an idempotent `dedupe_key`. Simulated fills are replayed at the end of confirm because they commit during dispatch. Alpaca paper fills arrive later from trade updates; a paper reconnect polls order status and re-enters that same fill-delta path. Live books are not mirrored. A failed post is retried on the next bridge poll.

If TSD is offline: SoftFail — app still trades via DaxAlgo OMS alone.

## Config

```json
"TsdBridge": {
  "Enabled": true,
  "BaseUrl": "http://127.0.0.1:8000",
  "PollIntervalSeconds": 2,
  "SoftFail": true
}
```

## Code

- `TradingTerminal.Infrastructure/TsdBridge/*`
- Options: `TsdBridgeOptions` in Core
- DI: `services.AddTsdBridge(configuration)` in `ServiceConfiguration`
- Research scan client: `POST /api/research/scan` + `GET /api/mdms/bars/history` (limit ≤ 5000)

## Two-gate

Authoring confirm / draft lock ≠ order authority. OMS risk may still `RiskRejected`.

## Fill mirror

`TsdFillMirror` posts only for paper registrations. The dedupe key is `{confirm dedupe_key or order id}:{OMS fill key}`. Partial fills are incremental. A cumulative catch-up (reconnect poll) posts only the unmirrored gap.

Deferred:

- A process restart drops in-memory registrations and the Alpaca adapter's tracked fill cursor, so historical fills are not replayed into TSD.
- `BridgeFillDto` does not carry `execution_owner`, `strategy_version`, or `grant_id`. Those remain on the pending confirm. TSD correlates the fill by `client_order_id`, `book_id`, `intent_version`, and `dedupe_key`.
- The intent is marked confirmed when dispatch is accepted, not when the fill lands.
- Alpaca crypto activity is still uncertified and does not become a fill mirror.
