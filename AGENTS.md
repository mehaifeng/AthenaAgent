# AGENTS.md - Athena.UI Project Context

This file serves as foundational guidance for AI interactions within the AthenaAgent workspace.

## 🚀 Project Overview

**Athena.UI** is a sophisticated, highly autonomous desktop AI assistant built with **.NET 10** and **Avalonia UI**. It is designed to be a "presence-like" intellectual partner with deep system integration capabilities, robust security, and a modern, polished interface.

### Core Technologies
- **Framework**: Avalonia UI 12.0 (Cross-platform XAML)
- **UI Themes**: `Semi.Avalonia` and `Irihi.Ursa.Themes.Semi` for a clean, modern aesthetic.
- **Runtime**: .NET 10
- **Architectural Pattern**: MVVM (using `CommunityToolkit.Mvvm`)
- **Dependency Injection**: `Microsoft.Extensions.DependencyInjection`
- **AI Integration**: OpenAI SDK 2.x (Chat, Embeddings, Image Generation, Audio/TTS, Tool Calling; shared retry and timeout policy)
- **Markdown Rendering**: `LiveMarkdown.Avalonia` for streaming chat output with inline images.
- **Browser Automation**: `Microsoft.Playwright` (Set-of-Marks visual grounding + vision-guided agent).
- **Audio**: no audio library. `SystemAudioService` shells out to the OS player (`afplay` / `powershell` / `mpg123`) through `ICliService`; there is deliberately no pause or seek. Do not reintroduce an audio component.
- **Logging**: Serilog with File and Console sinks.
- **Persistence**: JSON for config/history, Markdown for knowledge base, SQLite for vectors.

### Solution Layout
The repository is a multi-project solution (`Athena.UI.sln`):
- **Athena.UI** — the main desktop application (source lives at the repository root).
- **Athena.Updater** — standalone in-app updater executable (downloads/extracts GitHub releases; packaged alongside the app).
- **Athena.Archive.Tests** — test project covering conversation archive/history logic.
- **Athena.UI.HeadlessTests** — Avalonia headless UI assertion suite (see "Headless Tests" below).
- **Athena.Presentation.Tests** — dependency-free native PresentationML create/inspect/edit/validate round-trip and corruption suite.

Companion docs at the root: `AGENTS.md` (kept in sync with this file) and `README.md` / `README_CN.md`. Longer-form docs live in `Docs/` (user guides, `PrivacyPolicy.md`, `MinerU_API.md`). Release tooling lives in `release.sh` and `Scripts/`.

### Key Features
- **Unified OpenAI-Compatible Model Roles**: One shared provider holds Base URL/API key data. Main conversation, title generation, context compression, automatic approval, embedding, browser, sub-agent, maintenance, and virtual-pet (`Companion`, falls back to title generation) roles independently select a model. TTS and image generation retain extension-specific connections.
- **Direct Tool Calling**: The main model directly owns built-in and MCP tools. Their execution remains visible in grouped tool cards.
- **Parallel Sub-Agents ("Owl Village")**: The `dispatch_subagents` tool fans out a batch of isolated sub-agents (presets: `general`, `researcher`, `file_worker`) with per-type tool gating, orchestrated by `Services/SubAgents/`. Progress is visualized as an owl-village overview (`OwlVillageView`) plus a side dock, with walk animations and batch curtain-call dismissal.
- **Knowledge Base (Vector Database)**: Local Markdown-based memory stored in `AthenaData/KnowledgeBase`. Powered by synchronous SQLite vector persistence to ensure 100% "what-you-write-is-what-you-search" real-time consistency. Retrieval is hybrid (L2-normalized vectors + BM25/FTS) with heading/path context and file-level result aggregation; the store carries an embedding-model fingerprint and supports schema migration.
- **Knowledge Base Self-Maintenance**: Write-time duplicate-detection guardrails on `create_new_memory`, plus a periodic maintenance agent (`KnowledgeBaseMaintenanceService` / `KnowledgeBaseMaintenanceRunner`) that consolidates and prunes memory files in the background.
- **Context Management**: Centralized `TokenService` provides UI-wide atomic tracking of context window pressure, with visual warnings as the threshold approaches; `ContextCompressionService` uses the independently configured compression role.
- **Image Generation**: OpenAI-backed image generation with logical continuity across turns; generated images render inline within Markdown chat output.
- **Audio Output**: remote speech generation uses the OpenAI SDK `AudioClient` (mp3 — providers reject wav); playback runs through `SystemAudioService`, which shells out to the OS player (`afplay` / `powershell` / `mpg123`) via `ICliService`. Audio config is resolved per-model by `AudioConfigResolver`. No pause and no progress bar: both would require reintroducing an audio library.
- **Browser Automation**: Vision-guided agent (`Services/Browser/`) using Playwright + Set-of-Marks annotation to plan and execute multi-step web tasks via the `run_browser_task` tool; its model is selected by the unified Browser Agent role.
- **Native Office Authoring**: In-process OOXML services create, inspect, edit, convert/structure-edit where applicable, and validate DOCX, XLSX and PPTX without Python, Node, LibreOffice or Microsoft Office. Presentation validation includes relationship/content-type/chart checks plus conservative SkiaSharp text-fit estimates; rendered-slide QA remains mandatory.
- **Document Parsing**: MinerU-based document parser (`Services/Parsers/`) exposed via `DocumentParserFunctions` (`get_document_outline`, `parse_office_document`) for extracting outlines/content from attachments (see `Docs/MinerU_API.md`).
- **Screenshot Capture**: In-app screenshot tool with option to hide or retain the window during capture.
- **In-App Updates**: `GitHubUpdateService` checks GitHub releases; the separate `Athena.Updater` performs the swap. Windows releases ship both a zip (for in-app updates) and a bilingual Inno Setup installer. (macOS DMGs are unsigned/ad-hoc signed — see project memory on Gatekeeper/App Translocation.)
- **Conversation Archive & Rewind/Fork**: Sessions are persisted and browsable; any turn can be rewound or forked into a branch session (fork markers live on the branch side, attachments are physically cloned, sessions support master/branch nesting). Archive logic is covered by `Athena.Archive.Tests`.
- **Cron Scheduled Tasks**: Standard five-field cron (`minute hour day-of-month month day-of-week`) is the only scheduling semantics; six-field expressions with seconds are rejected because `CronExecutionWorker` checks once per whole minute. `Services/Cron/` splits the concerns deliberately: `CronScheduleService` evaluates expressions (Cronos never leaks out of it), `CronTaskStore`/`CronTaskService` own persistence and the run state machine, `CronExecutionWorker` pumps due runs (concurrency fixed at 2, FIFO), and `CronSessionLauncher` turns a claimed run into a session. **Every firing opens a brand-new session and never changes `SelectedConversation`** — that is the whole design, and `MainWindowViewModel.AttachScheduledSessionAsync` must never touch the selection. DI runs one way only (`worker → task service + launcher`); the launcher's host and the task page's navigator are attached by the main window at runtime, which is what keeps the graph acyclic. Missed occurrences are fixed-policy **Skip**: however many were crossed while the app was closed, exactly one `Skipped` record is written and the next run is computed from now — never a backlog of sessions at startup. `RunOnce` disables the task the moment it is *claimed* (not when it succeeds), so a failure is not retried. DST follows Cronos semantics, pinned by tests: a missing spring-forward time fires at the transition instant, an ambiguous fall-back time fires once for fixed-time expressions and twice for interval ones.
- **Virtual Pet ("Companion Loop")**: The window pet is an interaction surface: click to pat (or answer the need shown in the cue glyph), right-click for the care menu (pat / feed / play / say something / profile / rest / hide), drop files on it to attach them to the current conversation. Growth (mood / energy / exp + level, per-pet records in `AthenaData/pet_profile.json`) lives in the singleton `IVirtualPetProgressionService`, not in the per-session view model; mood and energy advance lazily from a timestamp so time passes while the app is closed. Cooldowns block the reward, not the feedback. Lines come from a local library, optionally replaced by a throttled `Companion`-role model call (off by default); the bubble is held open while that call is in flight (`ModelLineGrace` covers the 8 s `ModelTimeout`, which the 4.5 s `BubbleDuration` does not), and the 45 s minimum interval gates background chatter only, so an explicit "说句话" is never eaten by an automatic line — the hourly cap remains the spend ceiling. The profile panel is a light-dismiss `Popup`, and the "档案" menu item opens it from the context menu's `Closed` event rather than from a `Command`: a window has one shared `LightDismissOverlayLayer` and `MenuItem` raises `Click` before the menu closes, so opening the panel during the click lets the closing menu switch off the overlay the panel needs, killing click-outside-to-close. `TestVirtualPetProfileDismiss` guards the real path.
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
- **`SwitchLanguage` is global, so a test that switches languages must switch back.** `LocalizationService.SwitchLanguage` swaps the *application's* resource dictionaries, not just that instance's, so a case that loops over `["zh-CN", "en-US"]` and stops on English leaves every later UI assertion reading English while the suite asserts zh-CN. That is not a clean failure: the shell test's menu poll never matches, falls into the `await Task.Delay` path above, and the whole run **hangs with no message** instead of failing. End any language-looping case with `SwitchLanguage("zh-CN")`.
- **Iterate with a minimal repro first**: cases run sequentially in file order, so a test inserted mid-file takes ~100 s per full-suite iteration to reach. Validate new logic in isolation (construct VM + stub directly) before merging into the suite.
- Redirect output to a file and tail it — piping to `tail` block-buffers and hides live progress.

## 🏗️ Architecture & Structure

### Directory Map
- `Assets/Locales/`: Localized resource dictionaries (`Locale.en-US.axaml`, `Locale.zh-CN.axaml`).
- `Converters/`: XAML value converters.
- `Models/`: Data transfer objects, tool schemas, and prompt templates.
- `Services/`: Business logic, AI integration, and core infrastructure.
  - `Functions/`: Specific tool implementations (each registered in `FunctionRegistry.cs`):
    - `FileSystemFunctions`: Secure local file operations (read/write/modify/move/copy/delete, search-in-file, file info).
    - `KnowledgeBaseFunctions`: Semantic memory management (`create_new_memory`, `recall_from_memory`).
    - `WebSearchFunctions`: Real-time web information retrieval.
    - `CliFunctions`: Execution of safe terminal commands.
    - `CronTaskFunctions`: cron scheduled tasks (`create_task`, `update_task`, `list_tasks`, `cancel_task`, `run_task_now`). Schedules are cron expressions only — the old natural-language time parsing ("in 2 hours", "明天下午") is gone, and every failure returns structured validation so the model can correct itself instead of retrying the same bad input.
    - `ConfigurationFunctions`: Dynamic application setting inspection/updates (`view_self_configuration`, `modify_self_configuration`).
    - `ImageGenerationFunctions`: Image generation (`generate_image`) with cross-turn continuity.
    - `BrowserTaskFunctions`: Vision-guided web automation (`run_browser_task`).
    - `DocumentParserFunctions`: MinerU-backed attachment parsing (`get_document_outline`, `parse_office_document`).
    - `PresentationFunctions`: Native PresentationML inspection, creation, slide/text editing and static validation (`inspect_presentation`, `create_presentation`, `edit_presentation`, `validate_presentation`).
    - `SubAgentFunctions`: Parallel sub-agent dispatch (`dispatch_subagents`).
  - `Browser/`: Playwright-based browser agent — session management, action registry, vision service, Set-of-Marks annotator, and task planner.
  - `SubAgents/`: Sub-agent orchestration — `SubAgentOrchestrator`, `SubAgentRunner`, type presets (`SubAgentTypes`), per-type tool gating (`SubAgentToolGates`), model resolution, and owl-village zone layout.
  - `Cron/`: cron scheduling — `CronScheduleService` (expression evaluation, preview, description), `CronTaskStore` (atomic `cron_tasks.json` with per-record corruption isolation), `CronTaskService` (CRUD, claim/complete, Skip policy), `CronExecutionWorker` (minute-aligned checks, concurrency 2, FIFO), `CronSessionLauncher` (claimed run → new session).
  - `VirtualPet/`: pet growth — `VirtualPetProgressionService` (the only place mood/energy/exp change), `PetProfileStore` (atomic `pet_profile.json`, per-record corruption isolation), `InMemoryPetProfileStore` (designer/test), `PetChatterService` (local line library + throttled model lines). Signatures carry domain types only; the ViewModel does the projection.
  - `Parsers/`: Document parsing (`MinerUDocumentParserService`).
  - `Interfaces/`: Service abstractions for clean DI.
  - `Platform/`: OS-specific implementations (e.g., `DesktopPlatformPathService`).
  - Notable services: `OpenAIChatService`, `OpenAIEmbeddingService`, `OpenAIImageGenerationService`, `VectorStoreService`, `KnowledgeBaseMaintenanceService`, `TokenService`, `CronScheduleService` / `CronTaskService` / `CronExecutionWorker`, `SystemAudioService`, `ConversationArchiveService`, `AttachmentStoreService`, `GitHubUpdateService`, `ModelCatalogService`, `FunctionRegistry` / `ToolArgumentSchemaValidator`, `DocxPackageService`, `XlsxPackageService`, `PptxPackageService`, `DiffApplier` (powers `modify_system_file`).
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
  - `TasksViewModel`: cron task list projection, CRUD/pause/run-now, and run-record navigation. It holds no scheduling state and never reaches into `MainWindowViewModel`'s collections — jumping to a run's session goes through `IConversationNavigator`.
  - `CronTaskEditorViewModel`: shared create/edit editor. Its "next 5 runs" preview is load-bearing: a cron expression is unreadable, and that list is the only way a user can confirm they wrote what they meant.
  - `LogsViewModel`: Runtime system diagnostics.
  - `AboutViewModel`: Version and project info.
  - `AppSettingsWindowViewModel`: App Settings window composition without leaking the main-window DataContext.
  - `SkillsConnectorsWindowViewModel`: Skills/Connectors navigation and page lifetime owner.
  - `VirtualPetViewModel`: Pet projection and gesture orchestration. It owns no growth state — that lives in `IVirtualPetProgressionService` so it survives session switches.
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

### Review Rules — what "good" means in this repo

Ordered by **when the mistake surfaces**, not by how bad it feels. Silent failures come first because nothing else will ever report them. A rule earns its place only if it names a measured cost or a failure that actually happened here.

1. **A missing dependency must fail loudly, never degrade silently.** Constructor parameters that the production path requires are non-nullable; if a service is genuinely optional, the null branch must log. `Foo?` + `_foo?.Bar()` everywhere means a mis-wired composition root shows up as "the button does nothing" months later. Designer/test construction goes through an explicit `CreateForDesigner()`-style factory, never through a parameterless constructor that passes nulls for everything.
2. **Persistence is a whitelist, not whatever the type happens to expose.** `ChatMessage`, `ChatMessageSegment`, `ToolCallEntry` and `ChatAttachment` are simultaneously render models and the on-disk archive schema, so **every** computed property, transient UI flag, and `[RelayCommand]`-generated property must carry `[JsonIgnore]` / `[property: JsonIgnore]`. The authoritative definition of "persistent" is *what `ConversationPersistenceHelper.CloneMessage` copies*; anything it refuses to copy, and anything `PrepareRestoredMessage` resets, does not go to disk. This is enforced by `TestPersistedMessageFieldWhitelist` in `Athena.Archive.Tests` — when it fails, add `[JsonIgnore]`, do not extend the whitelist unless the field is genuinely restored. Measured before the guard existed: `ChatMessage` wrote 51 fields per message where 17 are actually restored; re-serializing the real 24-conversation / 794-message archive under the whitelist cut it from 6 589 718 to 4 067 605 bytes — **38.3 % of the archive was write-only state** (the store uses `WriteIndented = true`, so each junk field costs its own line). The derived properties duplicated `content` into `displayText` and the entire attachment array into `attachmentPanelItems`, and — because `ConversationArchiveStore` skips a write when `payload_hash` is unchanged — transient flags like `isLoading` flipping mid-stream defeated that write-skip and forced full-payload rewrites. Only the two properties that would have *crashed* the serializer (`LegacyMarkdownContent => this` recursing, `SegmentContent`) had ever been noticed; the other 25 failed silently for their whole lifetime. That asymmetry is the rule's whole point.
3. **Never block a thread waiting on the dispatcher.** No `Dispatcher.UIThread.InvokeAsync(...).GetAwaiter().GetResult()` / `.Result` / `.Wait()`, on either side. Make the method `async` and `await` it; if a synchronous entry point is unavoidable, marshal with `Post` and return without waiting. The app already burned itself on this once — see the comment on `MainWindowViewModel.PersistSessionStateAsync`, where the old blocking implementation deadlocked shutdown into a force-kill. **When you fix one instance of a hazard, sweep for the rest of them in the same commit**; that deadlock was fixed in one place and left standing in another for months.
4. **A service-layer signature may not name a ViewModel or an Avalonia type.** Look at the *signature*, not the `using`: `Services/Platform/*` are legitimate host adapters and may reference Avalonia inside their bodies. But an abstraction whose members are typed on a ViewModel — as `ISubAgentOrchestrator.ActiveAgents` once was (`ObservableCollection<SubAgentViewModel>`) — can never have a non-UI implementation, which cancels the decoupling at the point of declaration. Services expose domain state (`IReadOnlyList<SubAgentSnapshot>` + a change event); the ViewModel layer does the projection. `Services/Cron/` is the reference shape.
5. **`async void` is for real event handlers only, and the body is wrapped in `try/catch` that logs.** A timer callback or a `Task`-returning path that is `async void` turns any exception into an unhandled process-level crash. Prefer `async Task` awaited by the caller.
6. **An empty `catch` must name what it swallows and why it is safe.** `catch { }` with no comment is indistinguishable from a bug. If the answer is "this can't happen", log at debug and move on.
7. **A new invariant ships as an assertion, not as a comment.** Comments do not survive the next refactor; `Athena.Archive.Tests` / `Athena.UI.HeadlessTests` cases do. This is why rule 2 has a test and not a paragraph.
8. **Docs here are load-bearing spec.** `CLAUDE.md` / `AGENTS.md` are the only entry point an agent gets, so a stale claim produces wrong code directly. Deleting a component means editing every doc that claims it exists — the audio stack was replaced but both docs still advertised `LibVLCSharp`, and `AGENTS.md` pointed at a `CONTEXT.md` that had been deleted.
9. **Size is not the criterion; net direction is.** `MainConversationViewModel` is ~4 600 lines and that alone is not a finding. Ask whether the change *moves responsibility out* (as `ConversationExecutionCoordinator` and the `ICompression*` split did) or *deposits more in*. Do not split a class to hit a line count — it produces incohesive partials and fixes nothing.
10. **Do not relitigate decisions that already have measurements.** Message-list virtualization, the `.bubble-actions` hover transition, `ProcessRowTheme`, `ChatImageDetailLevel.High`, and the 5-field cron restriction were all decided against alternatives with recorded numbers. Overturning one requires better numbers, not a tidier-looking diff.

### Function Discovery Rules
When adding new tools:
1. Implement the logic in a class under `Services/Functions/`.
2. Register the tool in `FunctionRegistry.cs` with a clear description and JSON schema. **Descriptions must guide the AI clearly** (e.g., advising on when to use `write_system_file` vs `modify_system_file`).
3. If the tool modifies the Knowledge Base, it MUST trigger `RefreshVectorCacheAsync` to keep the vector database aligned with the file system.

## 🛡️ Operational Integrity & Security
- **Data Sandbox (`AthenaData`)**: All application state (config, logs, history, knowledge base) is strongly sandboxed within the `AthenaData/` folder located in the application's base directory.
- **System File Protection**: `FileSystemService` enforces strict security boundaries:
  - **Blacklists**: Hard blocks on critical paths (`/etc`, `/sys`, `C:\Windows`) and risky extensions (`.exe`, `.dll`, `.sys`).
  - **Self-Preservation**: AI tools are structurally prohibited from modifying the application's own `config.json` file.
  - **Quotas**: Limits on read/write sizes per operation to prevent memory exhaustion or disk flooding.
- **Tool Transparency & Lifecycle**: Tool executions provide visual feedback ("正在调用: xxx...") while executing, transitioning to ("xxx 调用完毕，持续思考中...") during subsequent network waits. Intermediate JSON tool-call messages are kept in context but hidden from the UI to avoid clutter.
