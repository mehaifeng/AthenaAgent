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
2. Register the tool in `FunctionRegistry.cs` with a clear description and JSON schema. **Descriptions must guide the AI clearly** (especially when a task should prefer `execute_terminal_command` over a specialized tool).
3. If the tool modifies the Knowledge Base, it MUST trigger `RefreshVectorCacheAsync` to keep the vector database aligned with the file system.

## 🔧 Function Calling (Tool Calling)

### 架构概览

AthenaAgent 使用 OpenAI SDK 的 `ChatTool` / `ChatToolCall` 实现 Function Calling。所有工具由 `FunctionRegistry` 集中注册和管理。

```
用户消息
    │
    ▼
ConversationContext.AddUserMessage()
    │
    ▼
BuildMessages(context)  ← 合并系统提示词（角色 + 平台 + 摘要 + 历史系统消息）
    │
    ▼
CreateChatOptions()  ← 将所有 ChatTool 定义注入 options.Tools
    │
    ▼
chatClient.CompleteChatStreamingAsync(messages, options)
    │
    ├─── [模型请求工具调用]
    │    │
    │    ▼
    │    ExecuteToolCallAsync() → FunctionRegistry.ExecuteAsync()
    │    │
    │    ▼
    │    工具结果 → ToolChatMessage → context.AddToolMessage()
    │    │
    │    └── 循环：重建消息列表，继续发送至 API，直到无更多工具调用
    │
    └─── [模型返回文本]
         ▼
         context.AddAssistantMessage()
```

### 工具注册机制

**文件:** `Services/Functions/FunctionRegistry.cs`

`FunctionRegistry` 通过私有方法 `RegisterFunction` 注册每个工具：

```csharp
var tool = ChatTool.CreateFunctionTool(
    name,
    description,
    BinaryData.FromString(JsonSerializer.Serialize(parameters))  // JSON Schema
);
_tools.Add(tool);
```

工具 Schema 为匿名对象的 JSON 序列化（如 `execute_terminal_command`）：

```json
{
  "type": "object",
  "properties": {
    "command": { "type": "string", "description": "Shell command or command chain." },
    "workingDirectory": { "type": "string", "description": "Working directory for the command." },
    "waitForExit": { "type": "boolean", "description": "Wait for completion; false for GUI/background tasks." }
  },
  "required": [ "command" ]
}
```

**可用工具:**

| 类 | 函数名 | 用途 |
|----|--------|------|
| `KnowledgeBaseFunctions` | `create_new_memory` | 创建新的知识库记录（`.md` + 向量嵌入） |
| | `recall_from_memory` | 语义向量检索 |
| `WebSearchFunctions` | `web_search` | 实时网络搜索 |
| `CliFunctions` | `execute_terminal_command` | 执行 Shell 命令，统一承担文件读写与其他系统操作 |
| `ConfigurationFunctions` | `modify_self_configuration` | 修改运行时配置（仅限白名单字段） |
| | `view_self_configuration` | 查看当前配置 |
| `ProactiveMessagingFunctions` | `create_task` | 创建定时提醒任务 |
| | `cancel_task` | 取消任务 |
| | `list_tasks` | 列出所有活动任务 |

### 上下文窗口影响（Token 计数）

**文件:** `Models/ConversationContext.cs`

`EstimatedTokenCount` 的计算方式：

```csharp
int total = EstimateTokens(_mainPersona);
total += ToolsDeclarationTokenCount; // 所有工具 Schema 只计一次
if (!string.IsNullOrEmpty(_summary)) total += EstimateTokens(_summary);
foreach (var msg in _messages)
{
    total += EstimateTokens(msg.Content);
    if (!string.IsNullOrEmpty(msg.ToolCallsJson))
        total += EstimateTokens(msg.ToolCallsJson); // 模型生成的工具调用 JSON
}
```

其中 `EstimateTokens(content)` = `content.Length / 2 + 10`（近似估算）。

**关键点：**
- **工具声明 Token**（`_cachedToolDeclarationTokens`）：所有工具的 Schema JSON 只在 `FunctionRegistry` 初始化时计算一次，**不随每轮对话增加**
- **工具调用参数/结果**：以 `ToolChatMessage` 存入 `ConversationContext`，每次循环都计入 token 总量
- **模型生成的 ToolCalls JSON**（`ToolCallsJson`）：也计入 token 总量

### 循环内中间压缩

**文件:** `Services/OpenAIChatService.cs` 第 138-172 行

当工具调用循环中 token 超过 `CompressionThreshold` 时，会在循环中途触发压缩：

```csharp
if (currentTokens > _config.CompressionThreshold)
{
    // 将 ContextMessage 转换为 ChatMessage 以供压缩服务处理
    // 压缩后更新摘要，清除被压缩的消息
    // 然后继续工具调用循环
}
```

这是为了防止多轮工具调用导致上下文溢出。

### 安全边界

模型侧的通用系统操作统一通过 `execute_terminal_command` 完成；知识库页面自己的文件读写则由专用的 `KnowledgeBaseFileService` 限定在 `AthenaData/KnowledgeBase` 目录内。
- **知识库边界**: UI 文件编辑仅允许访问知识库目录下的 Markdown 文件及其子目录
- **自我保护**: 主配置仍不应通过知识库文件服务触达
- **职责分离**: 模型侧文件操作走 CLI，UI 侧文件操作走知识库专用服务

## 🛡️ Operational Integrity & Security
- **Data Sandbox (`AthenaData`)**: All application state (config, logs, history, knowledge base) is strongly sandboxed within the `AthenaData/` folder located in the application's base directory.
- **Knowledge Base File Protection**: `KnowledgeBaseFileService` is a narrow UI-only service restricted to Markdown files inside `AthenaData/KnowledgeBase`.
- **Tool Transparency & Lifecycle**: Tool executions provide visual feedback ("正在调用: xxx...") while executing, transitioning to ("xxx 调用完毕，持续思考中...") during subsequent network waits. Intermediate JSON tool-call messages are kept in context but hidden from the UI to avoid clutter.
