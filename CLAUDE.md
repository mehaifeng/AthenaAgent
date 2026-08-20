# CLAUDE.md - Athena.UI Project Context

This file serves as foundational guidance for AI interactions within the AthenaAgent workspace.

## 🚀 Project Overview

**Athena.UI** is a sophisticated, highly autonomous desktop AI assistant built with **.NET 10** and **Avalonia UI**. It is designed to be a "presence-like" intellectual partner with deep system integration capabilities, robust security, and a modern, polished interface.

### Core Technologi
- **Framework**: Avalonia UI 12.0 (Cross-platform XAML)
- **UI Themes**: `Semi.Avalonia` and `Irihi.Ursa.Themes.Semi` for a clean, modern aesthetic.
- **Runtime**: .NET 10
- **Architectural Pattern**: MVVM (using `CommunityToolkit.Mvvm`)
- **Dependency Injection**: `Microsoft.Extensions.DependencyInjection`
- **AI Integration**: OpenAI SDK 2.x (Chat, Embeddings, Image Generation, Tool Calling)
- **Markdown Rendering**: `LiveMarkdown.Avalonia` for streaming chat output with inline images.
- **Browser Automation**: `Microsoft.Playwright` (Set-of-Marks visual grounding + vision-guided agent).
- **Audio**: `LibVLCSharp` for TTS / audio playback.
- **Logging**: Serilog with File and Console sinks.
- **Persistence**: JSON for config/history, Markdown for knowledge base, SQLite for vectors.

### Solution Layout
The repository is a multi-project solution (`Athena.UI.sln`):
- **Athena.UI** — the main desktop application (source lives at the repository root).
- **Athena.Updater** — standalone in-app updater executable (downloads/extracts GitHub releases; packaged alongside the app).
- **Athena.Archive.Tests** — test project covering conversation archive/history logic.
- **Athena.UI.HeadlessTests** — Avalonia headless UI assertion suite (see "Headless Tests" below).

Companion docs at the root: `AGENTS.md`, `CONTEXT.md`, and `README.md` / `README_CN.md`. Longer-form docs live in `Docs/` (user guides, `PrivacyPolicy.md`, `MinerU_API.md`). Release tooling lives in `release.sh` and `Scripts/`.

### Key Features
- **Unified OpenAI-Compatible Model Roles**: One shared provider holds Base URL/API key data. Main conversation, title generation, context compression, automatic approval, embedding, browser, sub-agent, and maintenance roles independently select a model. TTS and image generation retain extension-specific connections.
- **Direct Tool Calling**: The main conversation directly owns built-in and MCP tool calls. Each call is one collapsed `ToolCallRowView` row (status icon + summary, details on click).
- **Bubble Rendering Budget**: `MessagesItemsControl` is a plain `ItemsControl` inside a `ScrollViewer` — **nothing is virtualized**, so every message of a session is realized at load and the whole tree stays live while scrolling. That makes the per-row control count the thing that decides whether a long conversation scrolls: a collapsed process row (`ToolCallRowView` / `ReasoningRowView`) must not build its detail subtree (`ToolCallEntry.ExpandedDetails` / `ChatMessageSegment.ExpandedReasoning` return null when collapsed), status/chevron icons are one `PathIcon` switched by key through `ToolIconKeyToGeometryConverter` (never several with `IsVisible`), segment kinds are picked by `ChatSegmentTemplateSelector` (not four parallel `ContentControl`s), and the shared row styles live in `App.axaml` rather than on each control instance. A 20-assistant-message stress conversation with 240 tool rows measured 14 039 visuals / ~6.6 s first layout before these cuts and ~10 300 / ~2–3 s after; `TestAssistantBubbleLayoutVisual` holds the per-row budget. Virtualization was considered and rejected — variable-height bubbles make the scrollbar jump.
- **Hover Costs Scroll Frames**: scrolling with the cursor over a bubble moves content under a stationary pointer, so `:pointerover` flips element by element and every flip pays for style re-evaluation and repaint. Two rules, both measured with `ATHENA_SCROLL_PERF=1 dotnet Athena.UI.HeadlessTests/bin/Debug/net10.0/Athena.UI.HeadlessTests.dll` (a per-pointer-move benchmark that is not part of the assertion run): **never animate a hover reveal inside the message list** — the 0.15 s opacity transition on `.bubble-actions` cost 16.9 ms per message-row crossing versus 1.1 ms without it — and **never style it through a cross-element descendant selector** (`Grid.message-row:pointerover StackPanel.bubble-actions`); bind a class instead (`Classes.hovered="{Binding $parent[Grid].IsPointerOver}"`). Process rows use `ProcessRowTheme` (App.axaml) rather than the Semi button theme for the same reason: fewer nested styles to re-evaluate per flip. Wheel handling over a heavy bubble went from 46.6 ms to 2.1 ms per event. `IsVisible` would be cheaper still than opacity, but it changes layout — one hover would relayout the whole list.
- **Interleaved Assistant Bubble**: An assistant message is an ordered `ChatMessage.Segments` list — `Markdown` / `Reasoning` / `ToolCallGroup` / `GeneratedImage` — appended in arrival order, so a bubble reads as "thought → called these → said this → thought again". Nothing is hoisted to the top of the bubble: reasoning is per-round (one segment per round, auto-expand follows `AutoExpandReasoning`, auto-collapses when the round ends), and a tool group is just "the calls of one round" with no group-level collapse. `ChatMessage.ReasoningContent` still holds the accumulated text, but only as the replay copy for context/compression; display comes from the segments. Legacy archives (message-level reasoning, no segments) are migrated on restore by `ConversationPersistenceHelper.PrepareRestoredMessage` — **it must materialize `Content` into a Markdown segment first**, because the moment a message has any segment, `UsesSegmentLayout` flips and the legacy Markdown renderer stops drawing `Content`. Two placement rules keep arrival order honest when a provider interleaves reasoning and text inside one round: **a reasoning delta that arrives after streamed body text must open a new segment** (`reasoningSealedByText`) instead of growing the segment above the text — otherwise "what it thought after writing that" is back-dated into the box above it; and because that puts a Reasoning segment last, `CommitActiveBubbleRound` decides whether to materialize `Content` from `_activeTextMaterialized`, **never from "is the last segment Markdown"**, which would append a second copy of the same paragraph.
- **Parallel Sub-Agents ("Owl Village")**: The `dispatch_subagents` tool fans out a batch of isolated sub-agents (presets: `general`, `researcher`, `file_worker`) with per-type tool gating, orchestrated by `Services/SubAgents/`. Progress is visualized as an owl-village overview (`OwlVillageView`) plus a side dock, with walk animations and batch curtain-call dismissal.
- **Knowledge Base (Vector Database)**: Local Markdown-based memory stored in `AthenaData/KnowledgeBase`. Powered by synchronous SQLite vector persistence to ensure 100% "what-you-write-is-what-you-search" real-time consistency. Retrieval is hybrid (L2-normalized vectors + BM25/FTS) with heading/path context and file-level result aggregation; the store carries an embedding-model fingerprint and supports schema migration.
- **Knowledge Base Self-Maintenance**: Write-time duplicate-detection guardrails on `create_new_memory`, plus a periodic maintenance agent (`KnowledgeBaseMaintenanceService` / `KnowledgeBaseMaintenanceRunner`) that consolidates and prunes memory files in the background.
- **Context Management**: Centralized `TokenService` provides UI-wide atomic tracking of context window pressure, with visual warnings as the threshold approaches; `ContextCompressionService` uses the independently configured compression role.
- **Image Generation**: OpenAI-backed image generation with logical continuity across turns; generated images render inline within Markdown chat output.
- **Audio Output**: TTS playback via `LibVlcAudioPlaybackService` (toggle play/pause, stop), with audio config resolved per-model.
- **Browser Automation**: Vision-guided agent (`Services/Browser/`) using Playwright + Set-of-Marks annotation to plan and execute multi-step web tasks via the `run_browser_task` tool; its model is selected by the unified Browser Agent role. Three properties are load-bearing and easy to undo by accident: the agent **launches the machine's real Chrome/Edge first** (`RealBrowserChannels`) and only falls back to Playwright's bundled Chromium — the bundled one advertises `HeadlessChrome` and is what gets challenged by site protections; when it does fall back, the `HeadlessChrome` marker is stripped from the context user agent (UA Client Hints still leak it — turn off `Browser.Headless` if a site needs more). Screenshots go to the model at **`ChatImageDetailLevel.High`** with ~18px Set-of-Marks labels: `Low` squeezes the frame into 512px and the labels become unreadable, which makes the whole annotate/encode/store pipeline pointless. And `NetworkIdle` is awaited **only after a navigation**, never per observation — real sites never go idle, so a per-step wait burns its full timeout on every step.
- **Document Parsing**: MinerU-based document parser (`Services/Parsers/`) exposed via `DocumentParserFunctions` (`get_document_outline`, `parse_office_document`) for extracting outlines/content from attachments (see `Docs/MinerU_API.md`).
- **Screenshot Capture**: In-app screenshot tool with option to hide or retain the window during capture.
- **In-App Updates**: `GitHubUpdateService` checks GitHub releases; the separate `Athena.Updater` performs the swap. Windows releases ship both a zip (for in-app updates) and a bilingual Inno Setup installer. (macOS DMGs are unsigned/ad-hoc signed — see project memory on Gatekeeper/App Translocation.)
- **Conversation Archive & Rewind/Fork**: Sessions are persisted and browsable; any turn can be rewound or forked into a branch session (fork markers live on the branch side, attachments are physically cloned, sessions support master/branch nesting). Archive logic is covered by `Athena.Archive.Tests`.
- **Proactive Messaging**: Integrated task scheduler with recurrence support for reminders and follow-ups.
- **Multi-lingual**: Full runtime localization support (English, Chinese) via `AssetLoader` stream parsing.

## 🛠️ Building and Running

Ensure you have the .NET 10 SDK installed.

| Action | Command |
|--------|---------|
| **Restore** | `dotnet restore` |
| **Build** | `dotnet build` |
| **Run** | `dotnet run` |
| **Release** | `dotnet build -c Release` |
| **Headless Tests** | `Scripts/run-headless-tests.ps1` (PowerShell) or `Scripts/run-headless-tests.sh` (Git Bash/CI) |

### Headless Tests (`Athena.UI.HeadlessTests`)

Avalonia headless assertion suite — a console program with 60+ sequential cases (~2 min total). Assertion messages are written in zh-CN because the harness pins the zh-CN culture at startup.

- **Always run via `Scripts/run-headless-tests.ps1` / `.sh`, never `dotnet run`**: `dotnet run` rebuilds the whole solution, and a running Athena.UI instance locks `bin\Debug\net10.0\Athena.UI.exe`, failing the build (MSB3027). The scripts build with `-p:UseAppHost=false` (DLL only) and execute the suite DLL directly.
- **Exit code is meaningful**: the suite ends with `Environment.Exit(exitCode)` (headless Avalonia never shuts down on its own and the process would otherwise hang forever). All passing prints `[ALL HEADLESS TESTS PASSED]` and exits 0 — CI-able.
- **Never poll with `await Task.Delay` + `RunJobs` on the main thread**: the await continuation is posted to the dispatcher queue, and `RunJobs()` only runs after the await → deadlock. Use the `PumpUntil(done, timeoutMs, failureMessage)` helper for any condition waiting. The workbench tests still contain legacy `await Task.Delay` polling that happens to pass because their timers pump the dispatcher — **do not copy that pattern into new tests**.
- **Iterate with a minimal repro first**: cases run sequentially in file order, so a test inserted mid-file takes ~100 s per full-suite iteration to reach. Validate new logic in isolation (construct VM + stub directly) before merging into the suite.
- Redirect output to a file and tail it — piping to `tail` block-buffers and hides live progress.

##  Architecture & Structure

### Directory Map
- `Assets/Locales/`: Localized resource dictionaries (`Locale.en-US.axaml`, `Locale.zh-CN.axaml`).
- `Converters/`: XAML value converters.
- `Models/`: Data transfer objects, tool schemas, and prompt templates.
- `Services/`: Business logic, AI integration, and core infrastructure.
  - `Functions/`: Specific tool implementations (each registered in `FunctionRegistry.cs`):
    - `FileSystemFunctions`: Secure local file operations (read/write/modify/move/copy/delete, file info). `search_in_file` targets one file; `search_in_directory` is the cross-file search — it groups matches by file, prunes build/dependency directories and binaries, and is bounded by per-file/total match caps. Prefer it over listing a tree and searching file by file.
    - `KnowledgeBaseFunctions`: Semantic memory management (`create_new_memory`, `recall_from_memory`).
    - `WebSearchFunctions`: Real-time web information retrieval.
    - `CliFunctions`: Execution of safe terminal commands.
    - `ProactiveMessagingFunctions`: Automated task and reminder management (`create_task`, `list_tasks`, `cancel_task`).
    - `ConfigurationFunctions`: Dynamic application setting inspection/updates (`view_self_configuration`, `modify_self_configuration`).
    - `ImageGenerationFunctions`: Image generation (`generate_image`) with cross-turn continuity.
    - `BrowserTaskFunctions`: Vision-guided web automation (`run_browser_task`).
    - `DocumentParserFunctions`: MinerU-backed attachment parsing (`get_document_outline`, `parse_office_document`).
    - `SubAgentFunctions`: Parallel sub-agent dispatch (`dispatch_subagents`).
    - `DocumentFunctions`: Dependency-free WordprocessingML work backed by `Services/Documents/` (`inspect_document`, `create_document`, `edit_document`, `convert_document`, `validate_document`). Cross-run find/replace lives in `RunTextEditor`; styles and numbering in `DocxStyleLibrary`; `WordprocessingSchema` holds the ordered content models — always add property elements through `SetProperty`, since out-of-order children are what make Word offer to "repair" a file. The `docx` built-in skill only supplies routing and workflow discipline.
    - `SpreadsheetFunctions`: Dependency-free OOXML spreadsheet work backed by `Services/Spreadsheets/` (`inspect_spreadsheet`, `create_spreadsheet`, `edit_spreadsheet`, `modify_spreadsheet_structure`, `convert_spreadsheet`, `validate_spreadsheet`). Row/column edits rewrite formula references via `FormulaReferenceShifter`; styles are extended through `XlsxStyleLibrary`. The `xlsx` built-in skill only supplies routing and workflow discipline — it ships no scripts.
  - `Browser/`: Playwright-based browser agent — session management, action registry, vision service, Set-of-Marks annotator, and task planner.
  - `SubAgents/`: Sub-agent orchestration — `SubAgentOrchestrator`, `SubAgentRunner`, type presets (`SubAgentTypes`), per-type tool gating (`SubAgentToolGates`), model resolution, and owl-village zone layout.
  - `Ooxml/`: `OoxmlPackageService` — shared base for in-process OOXML editing (bounded ZIP access, part/relationship resolution, schema-ordered element insertion, atomic output). Format services derive from it; `Spreadsheets/` is the first, with docx/pptx planned.
  - `Parsers/`: Document parsing (`MinerUDocumentParserService`).
  - `Interfaces/`: Service abstractions for clean DI.
  - `Platform/`: OS-specific implementations (e.g., `DesktopPlatformPathService`).
  - Notable services: `OpenAIChatService`, `OpenAIEmbeddingService`, `OpenAIImageGenerationService`, `VectorStoreService`, `KnowledgeBaseMaintenanceService`, `TokenService`, `TaskScheduler` / `RecurrenceService`, `LibVlcAudioPlaybackService`, `ConversationArchiveService`, `AttachmentStoreService`, `GitHubUpdateService`, `ModelCatalogService`, `FunctionRegistry` / `ToolArgumentSchemaValidator`, `DiffApplier` (powers `modify_system_file`).
- `Styles/`: Global styles and icon geometries.
  - **Icons come from CoreUI Icons Free, never from Semi.** `Styles/CoreIcons.axaml` is
    generated — edit `Scripts/coreui-icons.manifest` and run
    `python3 Scripts/generate-coreui-icons.py`, never the .axaml by hand.
  - Views and view models bind only to the semantic `AthenaIcon*` aliases in
    `Styles/AppIcons.axaml`; binding a `CoreIcon*` key directly defeats the indirection that
    makes the vendor swappable. Dynamic icon choices go through a key string plus
    `ToolIconKeyToGeometryConverter` (see `ToolCallDisplay.IconKey`,
    `WorkspaceFileIcons.ForFileName`).
  - A missing `{StaticResource AthenaIconX}` neither fails the build nor throws — it silently
    leaves `PathIcon.Data` null and the icon disappears. `AssertEveryIconResolved` in the
    headless suite catches this on every captured window; do not weaken it.
  - Attribution is required (CC BY 4.0): see `Docs/ThirdPartyNotices.md`.
- `ViewModels/`: MVVM ViewModels.
  - `MainWindowViewModel`: Orchestrates the three-pane shell, workspace conversation tree, and feature windows.
  - `MainConversationViewModel`: Primary AI interaction interface; each conversation session owns one instance.
  - `KnowledgeBaseViewModel`: Local knowledge management, maintenance, and vector-index controls.
  - `TasksViewModel`: Proactive task list and scheduling.
  - `LogsViewModel`: Runtime system diagnostics.
  - `AboutViewModel`: Version and project info.
  - `AppSettingsWindowViewModel`: App Settings window composition without leaking the main-window DataContext.
  - `SkillsConnectorsWindowViewModel`: Skills/Connectors navigation and page lifetime owner.
  - `SubAgentViewModel`: Per-sub-agent state for the owl-village panel.
- `Views/`: Semantic XAML views corresponding to ViewModels, plus strongly typed feature windows and `OwlVillageView`.

### Coding Conventions
- **MVVM**: Strictly separate UI (`Views`) from logic (`ViewModels`).
- **DI**: Register all services in `App.axaml.cs`. Inject via constructor.
- **State Synchronization**: Avoid local state duplication. Use singleton services (like `ITokenService`) and bind `ViewModel` properties directly to these services.
- **Async/Await**: Use asynchronous patterns for I/O and AI calls. Avoid `Task.Run` for critical data persistence (like Vector indexing) to prevent race conditions.
- **Logging**: Use `Serilog` via constructor-injected `ILogger` or global `Log` class (File + Console sinks).
- **Attributes**:
  - `[ObservableProperty]`: For bindable fields in ViewModels. Ensure properties use PascalCase in bindings.
  - `[NotifyCanExecuteChangedFor]`: Apply to state variables (like `_isSending`) to automatically re-evaluate command availability.
  - `[RelayCommand]`: For command methods.
- **Localization**: Use `{loc:Loc KeyName}` in AXAML for translatable strings.

### Function Discovery Rules
When adding new tools:
1. Implement the logic in a class under `Services/Functions/`.
2. Register the tool in `FunctionRegistry.cs` with a clear description and JSON schema. **Descriptions must guide the AI clearly** (e.g., advising on when to use `write_system_file` vs `modify_system_file`).
3. If the tool modifies the Knowledge Base, it MUST trigger `RefreshVectorCacheAsync` to keep the vector database aligned with the file system.

## 🛡️ Operational Integrity & Security
- **Tool-Use Approval Gate**: A code-level approval chokepoint lives in `FunctionRegistry.ExecuteAsync`, so all three tool-execution paths (main chat, parallel sub-agents, KB maintenance) funnel through it and cannot bypass it. `ToolRiskClassifier` tiers every tool (ReadOnly / AdditiveWrite / Sensitive / Destructive), with `TerminalCommandRisk` doing command-level evaluation of `execute_terminal_command` (catches `bash -c "rm -rf"`, `curl|sh`, `sudo`, `chmod 777`, etc., and unwraps `shell -c "<payload>"` so wrapping cannot downgrade a destructive command). **AdditiveWrite** covers calls that can only create a new file and structurally cannot overwrite or delete (Office create/edit/convert with `overwrite` unset, `create_directory`); they auto-allow in Balanced and still prompt in Strict, and `overwrite=true` escalates them back to Sensitive. `ToolApprovalKey` scopes the dedup key so "always allow" is never blanket: terminal by command name, `fetch_url_to_file` by host, file writes by target directory (pre-existing unscoped entries keep working). `ToolRiskClassifier.IsNeverUnattended` blocks `modify_self_configuration` and MCP server management on unattended paths regardless of `SubAgentsInheritApproval` — otherwise a sub-agent could write `Security.ToolApprovalMode = Off` and disable the gate itself. `ToolApprovalContext` (AsyncLocal) selects the execution mode — Interactive (main chat → `ToolApprovalDialog` popup), NonInteractive (sub-agents → static policy: destructive always denied, sensitive gated by `SubAgentsInheritApproval`), Trusted (KB maintenance auto-allow), or Unset (fail-safe deny). `ToolApprovalService` owns the policy + Serilog audit; behavior is configurable via `ToolApprovalMode` (Off/Balanced/Strict, default Balanced), `AutoAllowedTools`, and `TerminalAllowlist` in the "Tools & Security" settings section. See `Docs/ToolApproval_Implementation_CN.md`.
- **Data Sandbox (`AthenaData`)**: All application state (config, logs, history, knowledge base) is strongly sandboxed within the `AthenaData/` folder located in the application's base directory.
- **System File Protection**: `FileSystemService` enforces strict security boundaries:
  - **Blacklists**: Hard blocks on critical paths (`/etc`, `/sys`, `C:\Windows`) and risky extensions (`.exe`, `.dll`, `.sys`).
  - **Self-Preservation**: AI tools are structurally prohibited from modifying the application's own `config.json` file.
  - **Quotas**: Limits on read/write sizes per operation to prevent memory exhaustion or disk flooding.
- **Tool Result Budget**: `FunctionRegistry.ExecuteAsync` is also the single chokepoint for result size (`Security.MaxToolResultChars`, default 60 000 chars). Oversized results are compressed by `ToolResultTruncator` — long array tails dropped first, then long text water-filled so short metadata (paths, paging cursors) always survives — and flagged with a `truncationNote`. Before this, only `execute_terminal_command` capped its output; `parse_office_document` in particular could return a whole book inline (it now takes an `outputPath` to write Markdown to disk and return only a heading map).
- **The tool list is a per-turn snapshot**: `CreateRequestRuntimeSnapshotAsync` builds `EffectiveRequestRuntimeSnapshot` (including `options.Tools`) **once per user turn**; the agentic loop in `ProcessStreamAsync` reuses it for every round. **A model therefore cannot acquire a tool mid-turn.** Any "call this tool to unlock more tools" design is broken here: the flag flips but nothing re-reads it, so the model is told a tool exists, never sees it, and burns the whole iteration budget calling a name the provider degrades to the nearest real tool. Whether Office tools ship is decided at snapshot time by `OfficeToolRelevance.IsRelevant(context)` — a pure function over the conversation, deliberately biased toward including them (a false positive costs tokens; a false negative breaks the task). `Extensions.OfficeToolsMode` = Auto/Always/Off. Keep `GetToolDeclarationTokenCount(includeOfficeTools)` fed by the same predicate or the context meter reports declarations that were never sent.
- **Runaway-Retry Guard**: `RepeatedToolFailureGuard` refuses a tool call after it has failed 3 times with byte-identical arguments, returning a result that names the dead end and lists the ways out. The iteration ceiling only guarantees the loop *eventually* stops; this stops it immediately. Tools must also be **idempotent where their postcondition allows it** — `create_directory` reports success on an existing directory (`mkdir -p` semantics, with a `created` flag), because reporting "already exists" as a failure gives the model nothing to change and is exactly what triggers a retry loop.
- **Parallel Tool Calls**: `ToolCallParallelism` batches a turn's tool calls (`Security.MaxParallelToolCalls`, default 4). Only *consecutive* read-only calls are batched — never reordered across a write — and only under Off/Balanced approval, where read-only calls provably never prompt. `run_browser_task` is the one non-read-only exception, batched under Off only (where nothing prompts): each task owns an isolated `BrowserContext` and the time is nearly all network and model waiting. For the same reason there is no longer a browser gate in `SubAgentToolGates` — the browser process is shared but its contexts are not, and `EnsureBrowserAsync` refuses to restart the process while sessions are live, which was the only real hazard. Results are always written back in the original order to satisfy the `tool_calls` pairing constraint. Set 1 to restore strictly sequential execution.
- **Tool Transparency & Lifecycle**: Tool executions provide visual feedback ("正在调用: xxx...") while executing, transitioning to ("xxx 调用完毕，持续思考中...") during subsequent network waits. Intermediate JSON tool-call messages are kept in context but hidden from the UI to avoid clutter.
