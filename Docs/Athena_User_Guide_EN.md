# Athena User Guide (English)

## 1. Scope
This guide focuses on four things only:
- What the Chat home page (`ChatTabView`) does.
- What each major Settings section means and how to configure it.
- How proactive messages work and how to set them up.
- How to guide conversations for long-running, coherent context quality with the knowledge base.

## 2. Chat Home (ChatTabView)
### 2.1 Core interactions
- Send message: press `Enter` or click Send.
- Streaming response: output arrives incrementally; you can stop generation at any time.
- New conversation: reset current thread and start clean.
- Per-message actions: copy, edit user message, regenerate assistant reply, delete message.

### 2.2 Context control
- Top bar shows token usage so you can monitor context growth.
- When threshold is reached, history can be compressed (summary + recent rounds kept).
- Draft restore: unfinished main conversation can be recovered after restart.

### 2.3 Why this matters
- Edit + regenerate lets you correct direction inside the same thread instead of fragmenting work.
- Stop response prevents wrong branches from polluting later turns.
- Compression + draft restore is the baseline for sustained multi-session collaboration.

## 3. Settings: Meaning and Configuration
Settings are auto-saved; no manual save button is required.

### 3.1 Appearance and interaction preferences
- Theme: UI look only.
- Language: UI strings and About-page documentation link routing.
- Skip confirmations (regenerate/edit): faster flow, but higher rollback risk.

Recommendation:
- Keep confirmations enabled at first, then disable once your workflow is stable.

### 3.2 Primary AI
- Provider / Endpoint / API Key: connectivity trio.
- Model: main conversation model.
- Temperature: creativity vs determinism.
- MaxTokens: per-response upper bound.
- EnableFunctionCalling: allow model to invoke tools.

Recommendation:
- For stable production-style work, use `Temperature` around 0.2 to 0.7.
- Always run connection test first.
- Pick the most stable model for your real workload, not only by benchmark.

### 3.3 Secondary and embedding models
- Secondary AI: used for background tasks such as summarization.
- Embedding: used by knowledge base semantic retrieval.

Recommendation:
- Secondary can be a cheaper/faster model.
- Keep embedding model stable; frequent switching can hurt vector consistency.

### 3.4 Memory and context compression
- MaxContextTokens: max context budget.
- CompressionThreshold: point where compression should trigger.
- AutoCompress: automatic compression switch.
- Compression preview / execute / undo summary: manual control for summary state.

Recommendation:
- Keep `AutoCompress` enabled for long threads.
- Set `CompressionThreshold` to roughly 40% to 70% of `MaxContextTokens`.
- For important projects, regularly verify that the summary still preserves key constraints.

## 4. Proactive Messages
Proactive messages are triggered by task scheduler events. At trigger time, Athena switches to Chat and injects a hidden trigger message so the assistant can initiate the interaction.

### 4.1 Setup steps
1. Open `Tasks` and create a task.
2. Set trigger time, intent, and recurrence.
3. Set task type to `Foreground / Proactive`.
4. At runtime, Chat will be activated and the intent will be executed.

### 4.2 Intent writing template (recommended)
Write intent as an executable instruction, not a vague reminder:
- Goal: what should be advanced.
- Context: project/state anchor.
- Output: expected response format.

Example:
- “Every day 09:30, review Athena doc progress and output: yesterday changes, today risks, next 3 actions.”

### 4.3 Usage notes
- Proactive is for interruptions you want to see and interact with.
- Background is for silent execution without interrupting chat flow.

## 5. Conversation Guidance for Long-Term Coherence

### 5.1 Make the first turn structured
Use these four blocks in your opening message:
- Goal: final deliverable.
- Constraints: stack, timeline, non-negotiables.
- Current state: progress, files, failures.
- Output format: checklist, steps, patch plan, validation list.

### 5.2 Advance one primary decision per turn
- Keep each turn focused on one key decision.
- Ask for the “smallest next action” at the end of each round.

### 5.3 Maintain explicit long-term anchors
Repeat these anchors over time:
- Canonical terminology.
- Hard constraints (for example compatibility and data integrity).
- Stage acceptance criteria.

This helps compression preserve what actually matters.

### 5.4 Work with the knowledge base deliberately
- Put stable facts in KB: standards, glossary, architecture boundaries, historical decisions.
- Keep volatile details in live chat: temporary plans, daily changes, active experiments.
- In prompts, explicitly say “follow knowledge base rules first” to reduce drift.

### 5.5 Correct drift quickly
Preferred order:
1. Stop response immediately.
2. Edit latest user message to restore constraints, then regenerate.
3. If needed, delete the wrong branch and continue from the last correct node.

## 6. Reusable High-Quality Prompt Template
Use this structure repeatedly:

```text
Goal:
Constraints:
Current state:
Please output first:
Done criteria:
```

Example:
```text
Goal: ship the log filtering and export flow.
Constraints: keep existing service interfaces; preserve current UI style; must be regression-safe.
Current state: level mapping is fixed; export interaction and list usability still need work.
Please output first: minimal change plan + file list, then implement.
Done criteria: build passes; core interactions validated; risks listed.
```

This structured format strongly improves answer stability and multi-turn continuity.
