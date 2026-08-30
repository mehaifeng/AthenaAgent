# Athena User Guide (English)

## 1. Scope
This guide focuses on the following:
- How the three-pane workspace and main conversation work (including attachments, screenshots, and sub-agents).
- What App Settings and Provider Models control.
- How to enable and manage Skills, MCP, voice, images, search, and document parsing.
- How cron scheduled tasks work (each firing opens its own new session) and how to set them up.
- How the Knowledge Base page works, and how the LLM uses it to actively learn your preferences.

## 2. Three-Pane Workspace and Main Conversation

![Three-pane workspace](images/MainShell.png)

The left pane owns workspaces and the conversation tree, the center permanently hosts the selected main conversation, and the right pane hosts workspace files, the workbench, and compact logs. Conversations can be pinned, renamed, forked, exported, searched, and deleted directly from the tree.

### 2.1 Core interactions
- Send message: press `Enter` or click Send.
- Streaming response: output arrives incrementally; you can stop generation at any time.
- New conversation: reset current thread and start clean.
- Per-message actions: copy, edit user message, regenerate assistant reply, delete message.
- Messages carry a send-time prefix for easier timeline review.

### 2.2 Attachments and document parsing
- Add local file attachments via the plus button on the left of the input box.
- MinerU document parsing: outlines and body content can be extracted from PDF and similar attachments for the assistant to reference.
- Built-in screenshot button: capture the screen with the option to hide or keep the Athena window.

### 2.3 Tool calls and sub-agents
- Tool executions show a visual status card (such as `dispatch_subagents` in the screenshot) while running and when finished; intermediate JSON messages stay in context but are hidden from the UI.
- Sub-agents: the assistant can dispatch multiple parallel sub-tasks via `dispatch_subagents` (file operations, web search, browser tasks, and more). Each sub-agent runs independently and results are merged back into the main conversation; progress is visualized in the "Owl City" panel.
- Browser tasks: `run_browser_task` runs a vision-guided browser agent that performs multi-step web operations.

### 2.4 Context control
- Top bar shows token usage (for example `9832 / 1280000 tokens`) so you can monitor context growth.
- When threshold is reached, history can be compressed (summary + recent rounds kept).
- Draft restore: unfinished main conversation can be recovered after restart.

## 3. App Settings and Provider Models

![App Settings window](images/AppSettingsWindow.png)

Settings are auto-saved; no manual save button is required.

### 3.1 Appearance and interaction preferences
- Theme: UI look only.
- Language: UI strings and About-page documentation link routing.
- Skip confirmations (regenerate/edit): faster flow, but higher rollback risk.

Recommendation:
- Keep confirmations enabled at first, then disable once your workflow is stable.

### 3.2 Provider Models
- Open **Provider Models** from the launcher to manage OpenAI-compatible providers, endpoints, API keys, and discovered model IDs.
- Assign models independently to main conversation, title generation, context compression, approval, Embedding, browser automation, sub-agents, knowledge maintenance, and image recognition.
- A compatibility section can retain an independent Embedding connection when required.

Recommendation:
- Refresh the provider model list to verify its endpoint and credentials.
- Pick the most stable model for your real workload, not only by benchmark.

### 3.3 Runtime, approvals, and diagnostics
App Settings owns context limits, automatic compression, workspace-knowledge budget, sub-agent concurrency, tool approval mode and allowlists, plus browser runtime/agent diagnostics.

Recommendation:
- Keep the embedding model stable; frequent switching can hurt vector consistency.

### 3.4 Memory and context compression
- MaxContextTokens: max context budget.
- CompressionThreshold: point where compression should trigger.
- AutoCompress: automatic compression switch.

Recommendation:
- Keep `AutoCompress` enabled for long threads.
- Set `CompressionThreshold` to roughly 40% to 70% of `MaxContextTokens`.

## 4. Skills & Connectors

![Skills and Connectors window](images/SkillsConnectorsWindow.png)

The independent Skills & Connectors window has six persistent sections: Skills, MCP, Speech, Image generation, Web Search, and Document parsing. Switching sections preserves each page's in-progress state, and changes are saved automatically.

- **Voice**: Enable voice, then configure the provider, API endpoint, API key, model name, and voice. Auto-play can be controlled separately; use **Diagnostics** to check the configuration.
- **Image generation**: Enable it to let the assistant use `generate_image`; provide its API endpoint, API key, and model name.
- **Web search**: Enable it to let the assistant use `web_search` for current information; configure the endpoint and credentials for the selected provider.

Recommendation: enable only the extensions you use. Keep API keys in local configuration and never paste them into ordinary chats or Knowledge Base files.

## 5. Skills

Skills give Athena reusable, specialized workflows for particular kinds of work.

1. Turn on the global **Enable Skills** switch.
2. Use **Import ZIP** or **Import folder** to add one Skill, or use **Open folder** to manage the local Skills directory directly.
3. Select **Refresh** to rescan; expand an item to review its source, compatibility, and description.
4. Use the switch beside a Skill to disable it temporarily; use Delete only when it is no longer needed.

Import a single Skill folder or ZIP archive that contains `SKILL.md`. Once imported, simply ask for a matching task in chat and Athena can select the Skill when appropriate.

## 6. MCP Servers

MCP (Model Context Protocol) lets Athena connect to external servers and use the tools and data they provide on demand.

1. Turn on the global **Enable MCP** switch.
2. Select **Add server** to configure a local command-based or remote HTTP/SSE MCP server, or paste Claude Desktop-format JSON and select **Import**.
3. After a successful connection, the list shows the number of discovered tools; expand a server for details.
4. Use the switch beside a server to control its availability. Delete removes the server and stops its local process.

Only add servers you trust. An MCP server may access local files, the network, or third-party services; review the requested action whenever an approval prompt appears.

## 7. Scheduled Tasks (cron)
Scheduled tasks are described with a standard five-field cron expression. **Every firing opens a brand-new session** instead of injecting a message into the chat you are looking at: the new session appears in the session tree with a clock icon, but the selection does not move, so you can keep working and read the result later.

### 7.1 Creation methods
1. Manual creation: open the Tasks page, fill in a name and an instruction, then pick a common preset (daily 09:00, weekdays 09:00, hourly, …) or write a cron expression directly. The editor shows a plain-language description and **the next 5 run times** as you type — a cron expression is unreadable, and that preview is your main way to confirm you wrote what you meant.
2. LLM-assisted creation: tell the LLM your goal and cadence in chat, and it will translate the cadence into a cron expression and create the task.

### 7.2 Writing the schedule
- The five fields are: minute, hour, day-of-month, month, day-of-week. For example `0 9 * * 1-5` = weekdays at 09:00, `*/30 * * * *` = every 30 minutes, `0 8 1 * *` = 08:00 on the 1st of each month.
- **Seconds are not supported**: the scheduler checks once per whole minute, so six-field expressions are rejected and the finest granularity is one minute.
- The time zone lives under **Advanced** and defaults to your system zone. The expression is evaluated in that zone, and run times are displayed in it.

### 7.3 Instruction template (recommended)
The new session has **no memory of your current chat**, so the instruction must stand alone:
- Goal: what should be advanced.
- Context: which project/workspace it applies to.
- Output: expected response format.

Example:
- "Review Athena doc progress and output: yesterday's changes, today's risks, next 3 actions."

### 7.4 Usage notes
- **Run only once**: runs at the first scheduled occurrence and then disables itself. It is never retried on failure.
- **Notify when it succeeds**: controls whether a successful run leaves an unread marker. Failures always show up regardless of this setting.
- **Missed runs are skipped, never backfilled**: occurrences crossed while the app was closed produce a single "Skipped" record, and the next run is computed from now — a pile of sessions at startup would be a disaster, not a compensation.
- **Run now** is an extra run: it also opens its own new session, but it does not consume or move the next scheduled occurrence.
- At most 2 scheduled sessions run at a time; the rest queue.
- Nothing fires while the app is closed (this is in-app scheduling, not an OS-level cron).
- Each task keeps its 20 most recent run records; the arrow beside a record jumps straight to the session that run created.

## 8. Knowledge Base and Long-Term Memory

![Knowledge Base window](images/KnowledgeBaseWindow.png)

### 8.1 The Knowledge Base page
- The left panel is the directory tree; create folders and Markdown files directly to organize long-term memory (for example `user_preferences`, `user_profiles`).
- The right panel shows and edits Markdown content; the full file path is displayed at the top.
- The toolbar provides refresh, import, export, delete, knowledge maintenance, and full vector-index rebuild operations.
- Knowledge base content is synchronously indexed into the vector database: what you write is immediately searchable.

### 8.2 Active memory behavior
- The LLM continuously learns your working style from both live conversations and knowledge base content — preferred structure, decision boundaries, and priority patterns — and actively reads/writes the knowledge base via `create_new_memory` / `recall_from_memory`.
- During long sessions, context compression keeps critical summaries so user preferences and project constraints stay available.
- With draft restore and continued sessions, the LLM builds a progressively more stable collaboration profile for you.

### 8.3 Long-term memory vs short-term memory
- The knowledge base stores long-term stable facts: terminology, standards, architecture boundaries, historical decisions.
- Live chat carries short-term dynamic facts: daily progress, temporary tactics, active risks.
- The LLM combines both layers automatically: stable constraints first, then latest state updates.

### 8.4 Minimal user effort
- You do not need to restate full constraints every turn; only provide updates when goals or boundaries change.
- If answers drift, one correction like "continue with existing KB rules and current goal" is usually enough to recover focus.
- Periodically promote newly stable rules into the knowledge base so the LLM can proactively reuse them next time.

## 9. Reusable High-Quality Prompt Template
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
