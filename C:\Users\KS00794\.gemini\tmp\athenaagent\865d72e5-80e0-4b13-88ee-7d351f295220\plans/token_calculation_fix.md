# Objective
1. 修复启动时 `ChatTabViewModel` Token 数量显示为 0 的 Bug。
2. 研究并优化 `ConversationContext` 中的 Token 估算逻辑，使其能够包含：完整的工具声明 (Tool Scheme)、对话中的工具调用 (Tool Calls) 以及工具的执行结果 (Tool Results)，从而实现对 OpenAI Token 消耗的准确模拟。

# Proposed Solution

## 阶段一：修复初始显示 Bug
在 `ChatTabViewModel.cs` 的 `InitializeAsync` 方法中，在调用 `await LoadSettingsAsync();` (该方法会设置 System Prompt) 之后，显式调用 `UpdateContextTokensDisplay();`，确保 UI 能立即读取并绑定到非零的初始 Token 值。

## 阶段二：重构 Token 估算逻辑

### 1. 调研现状
需要查看 `Models/ConversationContext.cs` 中的 `EstimatedTokenCount` 属性，以及 `Services/Functions/FunctionRegistry.cs` 如何存储工具声明。目前的估算大概率只简单统计了纯文本的长度并除以一个常数（如 3 或 4）。

### 2. 引入精确的模拟计算
OpenAI 对 Function Calling 的 Token 计算机制如下：
- **工具声明 (Functions/Tools)**：所有注册的工具会被序列化为 TypeScript-like 的声明语句，这部分占用极其庞大。我们需要获取 `FunctionRegistry` 中所有工具的 JSON 定义，将其转换为字符串并计算 Token。
- **消息中的工具调用 (assistant: tool_calls)**：不仅要计算普通的 `Content`，还要计算模型生成的工具函数名和 JSON 参数字符串。
- **工具结果 (tool: result)**：计算工具返回的结果字符串长度。

### 3. 具体修改
- **`ConversationContext.cs`**:
  - 修改 `EstimatedTokenCount`，引入对 `ToolCallsJson` 的长度计算。
  - 新增一个 `ToolsDeclarationTokenCount` 属性或直接在 `EstimatedTokenCount` 中加入一个外部注入的固定“工具库开销”基数。
- **`ChatTabViewModel.cs`**:
  - 在初始化时（或动态获取时），通过 `IFunctionRegistry` (如果可以注入的话，或者通过 `IChatService` 提供接口) 获取所有工具定义的序列化大小，将其加到总估算中。
  - 修改 `UpdateConversationContext`，确保 `ToolCallsJson` 和 `ToolCallId` 的长度被合理计入。

# Implementation Steps
1. 编写代码修复 `ChatTabViewModel.cs` 的初始化问题。
2. 研究 `ConversationContext.cs` 和 `FunctionRegistry.cs` 以确定如何获取工具声明的大小。
3. 更新 `ConversationContext` 和 `ChatMessage` 的 Token 估算算法。
4. 在 `ChatTabViewModel` 中将“工具声明开销”纳入总计（可能需要从依赖容器或服务中获取 `IFunctionRegistry` 的信息）。

# Verification
1. 启动程序，检查 `ChatTabView` 左上角的 Token 显示是否立刻变为非 0 值（如 2000+）。
2. 在对话中触发一次 Tool Call（如读取文件），观察 Token 增量是否显著（包括了 JSON 参数和返回结果的体积）。