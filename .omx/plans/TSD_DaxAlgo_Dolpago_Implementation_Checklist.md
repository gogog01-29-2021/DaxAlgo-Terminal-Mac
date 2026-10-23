# TSD × DaxAlgo × Dolpago — Implementation checklist (NOT the product spec)

**Canon (target spec):** [`TSD_DaxAlgo_Dolpago_Requirements_v1.md`](./TSD_DaxAlgo_Dolpago_Requirements_v1.md)  
IDs: **G / U01–U13 / S01–S06 / C01–C10 / Q01–Q11 / V01–V11**.

**Architecture lock:** TSD Agent OS × DaxAlgo Trading Kernel (adapter) — `/Users/w/Developer/tsd/.omx/plans/Architecture_TSD_Agent_OS_x_DaxAlgo_Kernel_2026-09-15.md`  
**Phase 5 lock:** [`Phase5_Execution_Contracts_2026-09-15.md`](./Phase5_Execution_Contracts_2026-09-15.md) · TSMS map: [`TSMS_Signal_OrderCandidate_Field_Map_2026-09-15.md`](./TSMS_Signal_OrderCandidate_Field_Map_2026-09-15.md)  
**MS-first order:** [`MS_First_Roadmap_2026-09-15.md`](./MS_First_Roadmap_2026-09-15.md) (① TSD body → ② DaxAlgo adapter; AI last)  
**External checklist crosswalk (F/W/E/R≠canon):** `/Users/w/Developer/tsd/.omx/plans/Requirements_Crosswalk_FWERCAX_2026-09-15.md`

This file tracks code progress only. **Nothing here upgrades a V-scenario to “passed” without the evidence named in the canon.**

Retired: old local rewrite R05/R07/R12 labels (conflicting scheme). Do not cite them.

---

## Progress vs canon (honest, 2026-09-15)

| Spec | Narrow code claim | Still open for that V/U |
|------|-------------------|-------------------------|
| U03 / C01 | Chart brush → Builder selection | Multi-asset shared context end-to-end |
| U04 / C02 | Indicator kind+period on sample (EMA 21 JSON) | Full param surface; no coercion proof in UI |
| U05 / C03 | Volume ≥ k×avg editable + version hash | AND/OR, crosses, multi-indicator |
| U06 | Local + TSD search (scan API / MDMS fallback) **+ both probability directions** (2026-09-26: `scan_volume_multiple` / `ResearchConditionEvaluatorV1` now return `outcome_given_feature` P(outcome\|feature) and `feature_given_outcome` P(feature\|outcome) as distinct numbers, not one hit-rate; ported to both TSD and local evaluators so they agree per R14; live-verified against running TSD server on real BTCUSDT bars, 0.684 vs 0.202 -- genuinely different, not a rounding artifact; 5 Python + 5 C# tests incl. a hand-computed series proving the two numbers can diverge) | Avalonia GUI not run to confirm the summary text renders on screen (dotnet build+test only, no visual click-through); AND/OR conditions (C03/R08) don't yet carry both directions through combination |
| U07 / C05 / V04 | Named refs A/B + session JSON + pending brush/bindings; VM reload + disk-shaped JSON; headless A≠B selections | Avalonia click→quit→reopen once still nice-to-have; see [`Volume_Rehearsal_Evidence_2026-09-15.md`](./Volume_Rehearsal_Evidence_2026-09-15.md) |
| U08 | LiveMeetsCondition + version badge | Eval time, stale/missing honesty (Q04) |
| U09 / C06 | Draft binds condition id+hash | Same verdict in generated strategy (V06) |
| U10 / V07 | — | Paper path not part of current slice |
| S03 / V05 | Live `:8000` scan k=2≠k=3 + MDMS bars/ohlcv/book/ticker; evidence doc | Avalonia click→UI; visualizer book still DaxAlgo hub (not MDMS) |
| U10 / S05 | Bridge + paper ledger assets exist | Explicit book/venue ownership before TOMS/PPMS claims |
| S06 / C08–C10 / V08–V11 | Next1–3 + **Next4** AI TargetIntent inside grant + durable `owner_lease` on paper ledger; SoftFail + paper confirm registers the OMS order; fill mirror posts real incremental fills (deduped) | Live confirms are not mirrored; `execution_owner` / `strategy_version` / `grant_id` stay on the pending confirm, not the fill body; LLM TradingDecision object not yet |
| MS-first / mock→real | Mac call graph documented; MDMS REST real; WS dashboard **was** random → now Binance public ticker; backtest fail-closed; bridge+research real; **Step 4** selection/condition/ref continuity (session reload) | optional visualizer→MDMS; TSD solo needs TOMS book ownership |
| Now B TSMS map | Field map written (Reuse/Extend/Gap) | Promote `strategy_version`, `book_id`, grant/dedupe onto host DTOs |
| Q08 | Separate risk-refusal test | Full confirm→OMS→fill with research path |
| V01 | — | Visualizer-only scenario not this slice |
| V02 | — | Multi-indicator situation study not this slice |

**Allowed first rehearsal name:** volume-condition research flow (maps toward U05–U07 + partial V03–V05).  
**Not allowed:** calling that rehearsal “product requirements complete.”

---

## Report template

```
Spec ID: …
User action: …
Code: …
Verification: …
Remaining vs canon: …
```
