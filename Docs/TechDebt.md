# Tech Debt Register

Known costs we have measured, understood, and deliberately not paid yet.

An entry earns a place here only if it names **what it actually costs**, **why the
obvious fix is not obviously right**, and **what evidence would settle it**. A vague
"this could be better" belongs in a commit message, not here. When an entry is paid
off, delete it and move the reasoning into `CLAUDE.md` — this file is a queue, not an
archive.

---

## 1. Switching conversations blocks the UI thread for one layout pass

**Status:** open · **Surfaced:** the conversation-switch veil work (2026-09)

### What it costs

Selecting another session in the left tree freezes the UI thread for roughly **2–3
seconds** on a large conversation (measured on a 20-assistant-message / 240-tool-row
stress conversation; see the Bubble Rendering Budget entry in `CLAUDE.md` for the
visual counts). During that window the app ignores input entirely: window drags,
button clicks and keystrokes all queue up and replay afterwards.

None of it is I/O. `InitializeConversationTreeAsync` restores every history into its
own view model at startup, so switching is purely `MessagesItemsControl` realizing one
conversation's whole bubble tree in a single blocking measure/arrange pass.

### What the veil does and does not fix

The switch veil makes the wait **legible**, not shorter:

- The row-selection animation now runs on an idle UI thread, because the first beat of
  a switch does nothing but select.
- The veil's own loading animation keeps moving through the freeze, because it runs as
  a composition animation on the render thread rather than on Avalonia's UI-thread
  animation clock.
- Rapid switches coalesce, so walking the list costs one rebuild instead of N.

What remains is the freeze itself. A user who clicks a session and then immediately
tries to drag the window still waits.

### Why the fix is not obvious

The only real fix is **incremental realization**: feed the message list in batches
across dispatcher turns so the thread yields between them. Three things make that more
than a local change:

1. **It collides with a decision that already has measurements.** Message-list
   virtualization was considered and rejected because variable-height bubbles make the
   scrollbar jump (`CLAUDE.md`, rule 10). Incremental realization is not virtualization
   — every message still ends up realized — but it lands in the same area and needs its
   own numbers rather than inheriting that rejection or overturning it by assertion.
2. **It needs a display collection separate from `ChatMessage` state.** The view binds
   `ItemsSource` straight to the conversation's own `Messages`. Filling in batches means
   introducing a second collection that mirrors it, which then has to stay correct
   across streaming appends, tool-round mutations, rewind/fork, search
   (`OnConversationSearchTextChanged` walks `session.Chat.Messages`) and persistence.
3. **Batched layout is not free either.** N smaller passes cost more total work than one
   large pass; the win is responsiveness, not throughput. Whether the trade reads as
   better depends on batch size and on how the scroll anchor behaves while content grows
   above the viewport — neither has been measured here.

### What would settle it

- A measurement of wall-clock-to-first-paint and worst-case single-turn block time for
  the current one-shot pass versus a batched pass, on the same stress conversation the
  rendering budget uses.
- Evidence that the scroll position stays put while later batches land — the same
  failure mode that sank virtualization.

If batching cannot keep the scrollbar still, the honest outcome is to keep the freeze
and keep the veil, and to say so here rather than re-opening it a third time.

---

## 2. A round larger than one compression pass can absorb is uncompressible, and the refusal blames something else

**Status:** open · **Surfaced:** support reconstruction on 2026-10-08 (dev install,
`deepseek/deepseek-v4.1-flash` through OrcaRouter)

### What it costs

Automatic compression fires, declines to plan, and the bubble says 「本次上下文压缩未成功，
回复照常继续（上下文未变）」 on **every** round, while "刷新本地 Plan" in the context
inspector answers 「无法生成压缩计划：…」. Nothing is broken and nothing changes — the
warning is simply permanent until the conversation ends, which reads as a stuck feature.

Measured on that install's own archive (270 messages, 5 rounds,
`AthenaData/conversations.db`) with the app's own estimator
(`ConversationContext.EstimateTokens`, CJK 1.0 / other 0.25 + 10 per message):

| round | messages | ~tokens |
|---|---|---|
| 0 | 7 | 2,506 |
| 1 | 11 | 17,352 |
| 2 | 191 | **274,626** |
| 3 | 8 | 3,473 |
| 4 | 63 | 78,651 |

Two independent ceilings refuse every window, and either alone is enough:

1. **Per-pass material.** A pass absorbs at most `summaryCeiling × ratio`, where
   `summaryCeiling = min(TargetSummaryTokens, OutputReserveTokens, Threshold / 4)`, i.e.
   16,000 × 8 = **128,000** tokens at Balanced and 256,000 at Aggressive. Round 2 is
   274,626 — larger than either ceiling. **No window containing it can ever be planned,
   at any setting.**
2. **Benefit threshold.** Round 2 can only be compressed by leaving it inside the recent
   retention window, and then the compressible material is the rounds *older* than it:
   19,858 tokens total, saving 17,375. The requirement is
   `max(1,024, 20% × PreCompressionEstimate)` = 20% × 237,899 = **47,580**.

`KeepRecentRounds` is therefore not the knob it looks like. Lowering it 3 → 1 frees
rounds 0–1 as well, and the planner then walks 4 rounds (297,957 tokens, ratio 18.6:1
over the 8:1 strength) → 3 rounds (294,484, 18.4:1) → 2 rounds (the same 17,375 benefit)
and stops on the benefit failure. The threshold (96,000 here) only decides **when to
try**; it never decides feasibility. Nothing in the settings window resolves this — keep
1/3 × Balanced/Aggressive were all simulated here and every one returns no plan.

### Why the fix is not obvious

- **Splitting a round across passes is the real fix, and it is not local.** A round is the
  atomic unit because `CompressionPlanner.BuildRoundGroups` / `IsCompleteRound` pair each
  `function_call` with its `tool` result and require `declared.SetEquals(resolved)`; a
  partial round would have to split where that pairing stays valid in *every* request the
  material is rebuilt for, and both the hard-anchor appendix and the benefit rule assume
  the material is the whole set. Note that `MaxMaterialPerPassTokens` already exists and
  is even shown in the settings summary, but **nothing enforces it** — the ratio rule fires
  first and reports a ratio failure, so the ceiling that actually decides the outcome is
  the one with no rule of its own.
- **Lowering the 20% benefit floor is the tempting one-liner.** It would admit the 2-round
  window (17,375 saved) and is a single constant. Against it: every attempt costs the
  compression model 20–175 s and real provider spend, and a pass saving under 20% of a
  context that is *already over budget* leaves that context still over budget after it
  commits.
- **Raising `OutputReserveTokens` would raise the per-pass ceiling**, but it is sized as a
  worst-case output budget by design — `MaxMaterialPerPassTokens`' own comment ties it to
  what the model can emit in one response — and a model that can emit 16K tokens cannot
  summarise 300K tokens into 16K without dropping facts. That trades a silent refusal for
  a silent quality loss.

### What would settle it

- A measurement of how much of round 2 is actually summarisable — repeatable tool-output
  JSON versus state the conversation still refers to. If it is mostly tool output,
  pass-splitting (or a tool-output-specific reduction) has a real target and this entry
  should be paid.
- A pass-splitting design that keeps call/result pairing valid per request, plus a
  validator run showing each partial summary is accepted on its own.
- Frequency: how often a single round exceeds the per-pass ceiling in real archives. One
  install is one data point; if it is rare, stating the ceiling beside the threshold is
  the honest fix.
- **Independently of the above, the refusal should name the blocker.** The dialog prints
  the *last* window's verdict — a benefit shortfall from the small rounds — and never
  mentions that a round exists no pass can absorb, so the natural reading ("raise the
  strength" / "keep fewer rounds") is precisely the set of settings that cannot help.
  That misattribution is what this entry cost in investigation time.
