# TSD real-fill mirror

## Goal

Mirror TSD-confirm fills from committed OMS fill events (real price and incremental quantity), paper/sandbox only. Stop posting the pre-trade mark estimate at confirm time.

## Plan

- `TsdFillMirror` registers a paper order and posts `BridgeFillDto` on each new fill, with an idempotent dedupe key and a cumulative watermark.
- `ExecutionCoordinator` notifies a fill observer only when a fill is newly appended (exact replay does not notify).
- Paper book runtimes forward those events. Confirm registers the order, then replays fills that already committed during synchronous simulated submit.
- Alpaca paper reconnect polls order status and re-enters the existing fill-delta path. Live mode does not.

## Blast radius

Infrastructure TsdBridge, Execution coordinator, ExecutionUi book runtimes, Avalonia confirm strip. No research-condition-search files. No live order routing changes.

## Build filter

`dotnet test tests/linux/TradingTerminal.Tests.Headless/TradingTerminal.Tests.Headless.csproj --filter Tsd`

## Tests

Headless: mirror unit cases, Alpaca fake trade updates (full, partial, replay, reconnect poll), simulated sync fill replay.

## Findings

Trade updates already land in OMS via `AlpacaExecutionAdapter.PublishFillDelta` and `ExecutionCoordinator`. The gap was the confirm strip posting `MarkPrice` and requested quantity immediately.

## Diff summary

Pending.

## Verification

Pending.

## Risks/deferred

- Process restart does not restore in-memory TSD registrations or the Alpaca tracked-fill cursor.
- `BridgeFillDto` still has no `execution_owner`, `strategy_version`, or `grant_id` (those stay on the pending confirm).
- Live confirms are not mirrored.
- Intent status is still set when dispatch is accepted, not when the fill lands.
