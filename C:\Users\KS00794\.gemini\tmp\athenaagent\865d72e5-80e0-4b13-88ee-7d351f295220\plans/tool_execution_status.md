# Objective
解决当 AI 进行纯工具调用时（或带有文本前缀的工具调用时），对话气泡显示空白或 `Loading` 状态过早消失导致应用仿佛“死机”的问题。需要确保无论是纯工具调用还是混合文本的工具调用，都能在气泡内向用户明确展示“正在执行工具”的状态，且不会与 AI 的实际回复文本混淆。

# Proposed Solution
1. **增强 `ChatMessage` 模型**：
   引入专门的 `ToolExecutionSummary` 属性（字符串），用于独立存储和展示工具执行的中间状态（例如“正在执行工具: read_system_file...”）。此属性的变更会自动通知 UI（通过 `HasToolExecutionSummary`）。

2. **改进 UI 呈现 (`ChatTabView.axaml`)**：
   在 `assistant` 和 `user` 气泡的结构中，将原有的单一 `Loading` 动画替换为更精细的 `StackPanel` 结构：
   - 顶部显示 AI 的实际回复文本（如果有）。
   - 中间显示带有旋转 Loading 图标的斜体工具状态提示（绑定到 `ToolExecutionSummary`）。
   - 底部保留普通的 "Thinking..." 动画，但当 `HasToolExecutionSummary` 为 true 时，隐藏普通的思考文字。

3. **调整流式响应与状态流转 (`ChatTabViewModel.cs`)**：
   在 `GetAiResponseAsync` 中，当接收到 `msg.Role == "assistant"` 且包含 `ToolCallsJson` 的消息时：
   - **不**立刻将 `IsLoading` 设为 `false`（保持脉冲动画）。
   - 解析 `ToolCallsJson` 提取被调用的工具名称，更新 `aiMessage.ToolExecutionSummary = "正在调用工具: xxx..."`。
   当接收到 `msg.Role == "tool"`（工具执行完毕返回结果）时：
   - 更新 `aiMessage.ToolExecutionSummary = "工具执行完毕，正在分析结果..."`。
   当下一轮流式文本真正开始返回时（即 `isFirstChunk` 被触发且有实质文本）：
   - 将 `ToolExecutionSummary` 清空，并将 `IsLoading` 设为 `false`。

# Implementation Steps
1. 在 `Models/ChatMessage.cs` 中增加 `ToolExecutionSummary` 和 `HasToolExecutionSummary`。
2. 在 `ViewModels/ChatTabViewModel.cs` 的回调函数中，添加工具名称的 JSON 解析，并进行 `ToolExecutionSummary` 的状态机流转控制。
3. 修改 `Views/ChatTabView.axaml` 中消息气泡的视图层级，使得 `ToolExecutionSummary` 和原有的 `IsLoading` 动画能和谐共存，且与 AI 内容明确分离。

# Verification
1. 测试一个纯粹的工具调用（如直接让 AI 查时间或读文件），观察气泡是否立刻出现并在下方显示“正在调用工具...”，并在完成后消失，最后被最终文本取代。
2. 测试一个带有前置思考文本的工具调用（如让 AI 先解释思路再去执行），验证前置文本能正常显示，同时下方依然能清晰地展示工具的执行进度。