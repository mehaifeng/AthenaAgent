# GEMINI.md - Athena.UI Project Context

This file serves as foundational guidance for AI interactions within the AthenaAgent workspace.

## 🚀 Project Overview

**Athena.UI** is a sophisticated, Avalonia-based desktop AI assistant built with **.NET 10**. It provides a professional, "presence-like" intellectual partner experience with a DOS-retro aesthetic.

### Core Technologies
- **Framework**: Avalonia UI 11.3 (Cross-platform XAML)
- **Runtime**: .NET 10
- **Architectural Pattern**: MVVM (using `CommunityToolkit.Mvvm`)
- **Dependency Injection**: `Microsoft.Extensions.DependencyInjection`
- **AI Integration**: OpenAI SDK (Chat, Embeddings, Tool Calling)
- **Logging**: Serilog with SQLite and Console sinks
- **Persistence**: JSON for config/history, Markdown for knowledge base

### Key Features
- **Streaming Chat**: Real-time response generation.
- **Direct Tool Calling**: Carries all available tool descriptions in every conversation, enabling immediate function invocation based on user intent.
- **Long-term Memory**: Markdown-based knowledge base with semantic search capability.
- **Proactive Messaging**: Integrated task scheduler for reminders and follow-ups.
- **Multi-lingual**: Full localization support (English, Chinese).
- **Memory Management**: Automatic and manual context compression using a secondary model for summarization.

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
- `Assets/`: Images, icons, and localized resource dictionaries (`.axaml`).
- `Converters/`: XAML value converters.
- `Markup/`: Custom XAML markup extensions (e.g., `LocExtension`).
- `Models/`: Data transfer objects and prompt templates.
- `Services/`: Business logic, AI integration, and core infrastructure.
  - `Functions/`: Specific tool implementations for the AI.
  - `Interfaces/`: Service abstractions.
  - `Platform/`: OS-specific implementations (e.g., path services).
- `Styles/`: Retro DOS styling definitions.
- `ViewModels/`: MVVM ViewModels.
- `Views/`: XAML UI definitions.

### Coding Conventions
- **MVVM**: strictly separate UI (`Views`) from logic (`ViewModels`).
- **DI**: Register all services in `App.axaml.cs`. Inject via constructor.
- **Async/Await**: Use asynchronous patterns for all I/O and AI calls.
- **Logging**: Use `Serilog` via constructor-injected `ILogger` or global `Log` class.
- **Attributes**:
  - `[ObservableProperty]`: For bindable fields in ViewModels.
  - `[RelayCommand]`: For command methods.
- **Localization**: Use `{loc:Loc KeyName}` in AXAML for translatable strings.

### Function Discovery Rules
When adding new tools:
1. Implement the logic in a class under `Services/Functions/`.
2. Register the tool in `FunctionRegistry.cs` with a clear description and JSON schema.
3. Update `ToolDiscoveryService` metadata if needed for vector-based discovery.

## 🛡️ Operational Integrity
- **Persistence Path**: Defaults to `~/.local/share/Athena/` on all platforms.
- **Security**: Never hardcode API keys. Use `AppConfig` via `ConfigService`.
- **Tool Transparency**: Tool executions should be "invisible" to the user unless explicitly required by the persona.
