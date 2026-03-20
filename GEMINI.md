# GEMINI.md - Athena.UI Project Context

This file serves as foundational guidance for AI interactions within the AthenaAgent workspace.

## 🚀 Project Overview

**Athena.UI** is a sophisticated, highly autonomous desktop AI assistant built with **.NET 10** and **Avalonia UI**. It is designed to be a "presence-like" intellectual partner with deep system integration capabilities, robust security, and a modern, polished interface.

### Core Technologies
- **Framework**: Avalonia UI 11.3 (Cross-platform XAML)
- **UI Themes**: `Semi.Avalonia` and `Irihi.Ursa.Themes.Semi` for a clean, modern aesthetic.
- **Runtime**: .NET 10
- **Architectural Pattern**: MVVM (using `CommunityToolkit.Mvvm`)
- **Dependency Injection**: `Microsoft.Extensions.DependencyInjection`
- **AI Integration**: OpenAI SDK (Chat, Embeddings, Tool Calling)
- **Logging**: Serilog with SQLite and Console sinks
- **Persistence**: JSON for config/history, Markdown for knowledge base, SQLite for vectors.

### Key Features
- **Tiered Multi-Model Architecture**:
  - **Primary Model**: Heavy lifting, tool execution, and complex reasoning (e.g., `gpt-4o`).
  - **Secondary Model**: Background tasks like history summarization and context compression (e.g., `gpt-4.1-mini` / `gpt-4o-mini`).
  - **Embedding Model**: Fast, cheap semantic mapping for the knowledge base (e.g., `text-embedding-3-small`).
- **Direct Tool Calling**: Carries all available tool descriptions in every conversation. Provides advanced UI state management during tool execution (smooth transitions, hidden intermediate steps, real-time summaries).
- **Knowledge Base (Vector Database)**: Local Markdown-based memory stored in `AthenaData/KnowledgeBase`. Powered by synchronous SQLite vector persistence to ensure 100% "what-you-write-is-what-you-search" real-time consistency.
- **Context Management**: Centralized `TokenService` provides UI-wide atomic tracking of context window pressure, with visual warnings as the threshold approaches.
- **Proactive Messaging**: Integrated task scheduler for reminders and follow-ups.
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
  - `Functions/`: Specific tool implementations:
    - `FileSystemFunctions`: Secure local file operations.
    - `KnowledgeBaseFunctions`: Semantic memory management.
    - `WebSearchFunctions`: Real-time web information retrieval.
    - `CliFunctions`: Execution of safe terminal commands.
    - `ProactiveMessagingFunctions`: Automated task and reminder management.
    - `ConfigurationFunctions`: Dynamic application setting updates.
  - `Interfaces/`: Service abstractions for clean DI.
  - `Platform/`: OS-specific implementations (e.g., `DesktopPlatformPathService`).
- `Styles/`: Global styles and icon stream geometries.
- `ViewModels/`: MVVM ViewModels.
  - `MainWindowViewModel`: Orchestrator for all tabs.
  - `ChatTabViewModel`: Primary AI interaction interface.
  - `KnowledgeBaseTabViewModel`: Local knowledge management UI.
  - `TasksTabViewModel`: Proactive task list and scheduling.
  - `HistoryTabViewModel`: Conversation history browser.
  - `ConfigTabViewModel`: User preferences and API settings.
  - `LogsTabViewModel`: Runtime system diagnostics.
  - `AboutTabViewModel`: Version and project info.
- `Views/`: XAML UI definitions corresponding to ViewModels.

### Coding Conventions
- **MVVM**: Strictly separate UI (`Views`) from logic (`ViewModels`).
- **DI**: Register all services in `App.axaml.cs`. Inject via constructor.
- **State Synchronization**: Avoid local state duplication. Use singleton services (like `ITokenService`) and bind `ViewModel` properties directly to these services.
- **Async/Await**: Use asynchronous patterns for I/O and AI calls. Avoid `Task.Run` for critical data persistence (like Vector indexing) to prevent race conditions.
- **Logging**: Use `Serilog` via constructor-injected `ILogger` or global `Log` class.
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
