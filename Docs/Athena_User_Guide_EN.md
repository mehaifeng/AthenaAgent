# Athena User Guide (English)

## 1. Scope
This guide focuses on four things only:
- What the Chat home page does.
- What each major Settings section means and how to configure it.
- How proactive messages work and how to set them up.
- How LLM actively learns user preferences through the knowledge base and maintains long-term context coherence.

## 2. Chat Home
### 2.1 Core interactions
- Send message: press `Enter` or click Send.
- Streaming response: output arrives incrementally; you can stop generation at any time.
- New conversation: reset current thread and start clean.
- Per-message actions: copy, edit user message, regenerate assistant reply, delete message.

### 2.2 Context control
- Top bar shows token usage so you can monitor context growth.
- When threshold is reached, history can be compressed (summary + recent rounds kept).
- Draft restore: unfinished main conversation can be recovered after restart.

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
Proactive messages are triggered by the system scheduling mechanism. At trigger time, Athena switches to Chat and injects a hidden trigger message so the assistant can initiate the interaction.

### 4.1 Creation methods
You can create proactive messages in two ways:
1. Manual creation: open `Tasks`, create a task, set trigger time, intent, and recurrence, then set task type to `Foreground / Proactive`.
2. LLM-assisted creation: directly tell the LLM your reminder goal and schedule in chat, and it will create the proactive task through built-in system mechanisms.

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

## 5. How LLM Learns You Through the Knowledge Base

### 5.1 Active memory behavior
- LLM continuously learns your working style from both live conversations and knowledge base content, including preferred structure, decision boundaries, and priority patterns.
- During long sessions, context compression keeps critical summaries so user preferences and project constraints stay available.
- With draft restore and continued sessions, LLM builds a progressively more stable collaboration profile for you.

### 5.2 Long-term memory vs short-term memory
- Knowledge base stores long-term stable facts: terminology, standards, architecture boundaries, historical decisions.
- Live chat carries short-term dynamic facts: daily progress, temporary tactics, active risks.
- LLM combines both layers automatically: stable constraints first, then latest state updates.

### 5.3 Minimal user effort
- You do not need to restate full constraints every turn; only provide updates when goals or boundaries change.
- If answers drift, one correction like “continue with existing KB rules and current goal” is usually enough to recover focus.
- Periodically promote newly stable rules into the knowledge base so LLM can proactively reuse them next time.

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
