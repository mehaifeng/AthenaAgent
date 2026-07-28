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
- **Audio**: `LibVLCSharp` for TTS / audio playback.
- **Logging**: Serilog with File and Console sinks.
- **Persistence**: JSON for config/history, Markdown for knowledge base, SQLite for vectors.

### Solution Layout
The repository is a multi-project solution (`Athena.UI.sln`):
- **Athena.UI** — the main desktop application (source lives at the repository root).
- **Athena.Updater** — standalone in-app updater executable (downloads/extracts GitHub releases; packaged alongside the app).
- **Athena.Archive.Tests** — test project covering conversation archive/history logic.

Companion docs at the root: `AGENTS.md`, `CONTEXT.md`, and `README.md` / `README_CN.md`. Longer-form docs live in `Docs/` (user guides, `PrivacyPolicy.md`, `MinerU_API.md`). Release tooling lives in `release.sh` and `Scripts/`.

### Key Features
- **Unified OpenAI-Compatible Model Roles**: One shared provider holds Base URL/API key data. Main conversation, title generation, context compression, automatic approval, embedding, browser, sub-agent, and maintenance roles independently select a model. TTS and image generation retain extension-specific connections.
- **Direct Tool Calling**: The main model directly owns built-in and MCP tools. Their execution remains visible in grouped tool cards.
- **Parallel Sub-Agents ("Owl Village")**: The `dispatch_subagents` tool fans out a batch of isolated sub-agents (presets: `general`, `researcher`, `file_worker`) with per-type tool gating, orchestrated by `Services/SubAgents/`. Progress is visualized as an owl-village overview (`OwlVillageView`) plus a side dock, with walk animations and batch curtain-call dismissal.
- **Knowledge Base (Vector Database)**: Local Markdown-based memory stored in `AthenaData/KnowledgeBase`. Powered by synchronous SQLite vector persistence to ensure 100% "what-you-write-is-what-you-search" real-time consistency. Retrieval is hybrid (L2-normalized vectors + BM25/FTS) with heading/path context and file-level result aggregation; the store carries an embedding-model fingerprint and supports schema migration.
- **Knowledge Base Self-Maintenance**: Write-time duplicate-detection guardrails on `create_new_memory`, plus a periodic maintenance agent (`KnowledgeBaseMaintenanceService` / `KnowledgeBaseMaintenanceRunner`) that consolidates and prunes memory files in the background.
- **Context Management**: Centralized `TokenService` provides UI-wide atomic tracking of context window pressure, with visual warnings as the threshold approaches; `ContextCompressionService` uses the independently configured compression role.
- **Image Generation**: OpenAI-backed image generation with logical continuity across turns; generated images render inline within Markdown chat output.
- **Audio Output**: Remote speech generation uses the OpenAI SDK `AudioClient`; playback runs through `LibVlcAudioPlaybackService` (toggle play/pause, stop), with audio config resolved per-model.
- **Browser Automation**: Vision-guided agent (`Services/Browser/`) using Playwright + Set-of-Marks annotation to plan and execute multi-step web tasks via the `run_browser_task` tool; its model is selected by the unified Browser Agent role.
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
    - `ProactiveMessagingFunctions`: Automated task and reminder management (`create_task`, `list_tasks`, `cancel_task`).
    - `ConfigurationFunctions`: Dynamic application setting inspection/updates (`view_self_configuration`, `modify_self_configuration`).
    - `ImageGenerationFunctions`: Image generation (`generate_image`) with cross-turn continuity.
    - `BrowserTaskFunctions`: Vision-guided web automation (`run_browser_task`).
    - `DocumentParserFunctions`: MinerU-backed attachment parsing (`get_document_outline`, `parse_office_document`).
    - `SubAgentFunctions`: Parallel sub-agent dispatch (`dispatch_subagents`).
  - `Browser/`: Playwright-based browser agent — session management, action registry, vision service, Set-of-Marks annotator, and task planner.
  - `SubAgents/`: Sub-agent orchestration — `SubAgentOrchestrator`, `SubAgentRunner`, type presets (`SubAgentTypes`), per-type tool gating (`SubAgentToolGates`), model resolution, and owl-village zone layout.
  - `Parsers/`: Document parsing (`MinerUDocumentParserService`).
  - `Interfaces/`: Service abstractions for clean DI.
  - `Platform/`: OS-specific implementations (e.g., `DesktopPlatformPathService`).
  - Notable services: `OpenAIChatService`, `OpenAIEmbeddingService`, `OpenAIImageGenerationService`, `VectorStoreService`, `KnowledgeBaseMaintenanceService`, `TokenService`, `TaskScheduler` / `RecurrenceService`, `LibVlcAudioPlaybackService`, `ConversationArchiveService`, `AttachmentStoreService`, `GitHubUpdateService`, `ModelCatalogService`, `ToolDiscoveryService`, `DiffApplier` (powers `modify_system_file`).
- `Styles/`: Global styles and icon stream geometries.
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
- **Data Sandbox (`AthenaData`)**: All application state (config, logs, history, knowledge base) is strongly sandboxed within the `AthenaData/` folder located in the application's base directory.
- **System File Protection**: `FileSystemService` enforces strict security boundaries:
  - **Blacklists**: Hard blocks on critical paths (`/etc`, `/sys`, `C:\Windows`) and risky extensions (`.exe`, `.dll`, `.sys`).
  - **Self-Preservation**: AI tools are structurally prohibited from modifying the application's own `config.json` file.
  - **Quotas**: Limits on read/write sizes per operation to prevent memory exhaustion or disk flooding.
- **Tool Transparency & Lifecycle**: Tool executions provide visual feedback ("正在调用: xxx...") while executing, transitioning to ("xxx 调用完毕，持续思考中...") during subsequent network waits. Intermediate JSON tool-call messages are kept in context but hidden from the UI to avoid clutter.
