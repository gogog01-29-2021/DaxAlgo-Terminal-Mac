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

Paper confirm registers the OMS order and replays committed fills. Later fills notify `IExecutionFillObserver` and `TsdFillMirror` posts the incremental quantity. Alpaca paper connect and book reconcile poll order status back through `PublishFillDelta`. Reconciliation snapshots no longer advance that fill cursor ahead of the unpublished gap.

## Verification

`dotnet test tests/linux/TradingTerminal.Tests.Headless/TradingTerminal.Tests.Headless.csproj --filter "FullyQualifiedName~TsdFill|FullyQualifiedName~TsdRealFill|FullyQualifiedName~TsdBridgeSoftFail|FullyQualifiedName~LiveExecutionConsoleFlow"` — 17 passed.

`dotnet test tests/linux/TradingTerminal.App.Avalonia.Tests/TradingTerminal.App.Avalonia.Tests.csproj --filter FullyQualifiedName~ExecutionConsoleViewModelConfirmIntoOms` — 5 passed. The App.Avalonia suite also carries pre-existing research-condition-search failures that this change does not touch.

`dotnet test tests/linux/TradingTerminal.Tests.Headless/TradingTerminal.Tests.Headless.csproj --filter FullyQualifiedName~LiveExecution` — 19 passed.

## Risks/deferred

- Process restart does not restore in-memory TSD registrations or the Alpaca tracked-fill cursor.
- `BridgeFillDto` still has no `execution_owner`, `strategy_version`, or `grant_id` (those stay on the pending confirm).
- Live confirms are not mirrored.
- Intent status is still set when dispatch is accepted, not when the fill lands.
- The macOS context index still predates the Execution projects. A full regen rewrites every shard fingerprint, so it was left unchanged.
