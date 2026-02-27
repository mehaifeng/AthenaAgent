# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Athena.UI is an Avalonia-based desktop AI assistant application built with .NET 10. It features OpenAI integration with streaming responses, a two-stage Function Calling system with vector-based tool discovery, knowledge base management, and task scheduling capabilities.

## Build Commands

```bash
# Build the project
dotnet build

# Run the application
dotnet run

# Build for release
dotnet build -c Release
```

## Architecture Overview

### MVVM Pattern
- **Views**: Avalonia AXAML files in [Views/](Views/) - UI definitions only
- **ViewModels**: Located in [ViewModels/](ViewModels/) using CommunityToolkit.Mvvm with `[ObservableProperty]` and `[RelayCommand]` attributes
- **Models**: Data models in [Models/](Models/) with observable properties

### Dependency Injection
All services are registered in [App.axaml.cs](App.axaml.cs) in `ConfigureServices()`. Services are injected via constructor parameters. Access services globally via `App.Services`.

### Two-Stage Function Calling System

The application implements a unique two-stage tool discovery mechanism:

1. **Stage 1 - Meta Tool**: The AI always starts with only the `discover_tools` meta-tool available
2. **Stage 2 - Dynamic Discovery**: When `discover_tools` is called, [ToolDiscoveryService](Services/ToolDiscoveryService.cs) uses vector similarity search to find relevant tools based on intent, then returns only those tools to the AI

This approach prevents context pollution from unused tools and improves relevance.

### Key Services

| Service | Purpose |
|---------|---------|
| `OpenAIChatService` | Handles streaming chat with OpenAI API, manages tool calling flow |
| `ToolDiscoveryService` | Vector-based tool discovery using embeddings |
| `KnowledgeBaseService` | Manages user knowledge files with vector search |
| `OpenAIEmbeddingService` | Generates embeddings for semantic search |
| `TaskScheduler` | Schedules and triggers proactive messages |
| `ConversationHistoryService` | Persists conversation history with summaries |
| `PromptService` | Manages system prompts and templates |
| `ConfigService` | Application configuration with JSON persistence |

### Function Categories

Functions are organized in [Services/Functions/](Services/Functions/):

- **KnowledgeBaseFunctions**: CRUD operations on knowledge files, semantic search
- **ProactiveMessagingFunctions**: Schedule, cancel, list reminders/tasks
- **ConfigurationFunctions**: Read/modify app settings

### Data Storage

- **Configuration**: `~/.local/share/Athena/config.json`
- **Knowledge Base**: `~/.local/share/Athena/KnowledgeBase/` (Markdown files + SQLite vectors)
- **Conversation History**: `~/.local/share/Athena/history/*.json` (one JSON file per conversation)
- **Logs**: `~/.local/share/Athena/Logs/logs.db`

## Key Files

- [App.axaml.cs](App.axaml.cs): DI container setup, service registration
- [MainWindowViewModel.cs](ViewModels/MainWindowViewModel.cs): Main UI logic, message handling
- [OpenAIChatService.cs](Services/OpenAIChatService.cs): Core chat implementation with streaming
- [FunctionRegistry.cs](Services/Functions/FunctionRegistry.cs): Registers all available tools
- [ToolDiscoveryService.cs](Services/ToolDiscoveryService.cs): Vector-based tool discovery

## Development Notes

### Adding a New Function

1. Add the function method to appropriate `*Functions.cs` class in [Services/Functions/](Services/Functions/)
2. Register it in `FunctionRegistry` constructor with name, delegate, description, and JSON schema
3. Add metadata (category, use cases, tags) in `ToolDiscoveryService.GetAllToolDefinitions()`

### MVVM Conventions

- Use `[ObservableProperty]` for bindable properties (generates `PropertyName` property from `_propertyName` field)
- Use `[RelayCommand]` for commands (generates `CommandNameCommand` from `CommandNameAsync()` method)
- ViewModels inherit from `ViewModelBase`

### UI Framework

- Avalonia 11.3 with Fluent theme
- Markdown rendering via `Markdown.Avalonia` package
- Compiled bindings enabled by default (`AvaloniaUseCompiledBindingsByDefault`)
