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
