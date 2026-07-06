# 工具使用审批（Tool-Use Approval）实施报告

> 编写日期：2026-07-05
> 范围：从工具执行链路、风险分级、配置模型，到审批弹窗 UI 与无头调用路径的落地方案
> 依据：对 `OpenAIChatService` / `FunctionRegistry` / `CliFunctions` / `ChatTabViewModel` / `ConfirmDialog` / `SubAgentRunner` 的代码走查
> 关联评审：`Docs/ProductReview_CN.md` 第一条「安全模型自相矛盾」

---

## ✅ 落地状态（2026-07-05，已按 P2 目标实施）

已直接按 **P2 加固架构** 实现并通过测试（全量 39 项测试通过、整解决方案 0 error 构建）：

- **单一 chokepoint**：审批闸门下沉到 `FunctionRegistry.ExecuteAsync`，主对话 / 并发子代理 / 知识库维护三条路径无法绕过。被拒返回失败 `FunctionResult`，天然被各调用方转成 tool 结果，满足工具协议（无需改三处循环）。
- **AsyncLocal 执行模式**：`ToolApprovalContext` 区分 Interactive（主对话弹窗）/ NonInteractive（子代理静态策略）/ Trusted（KB 维护自动放行）/ Unset（fail-safe 拒绝）。
- **风险分级 + 终端命令高危模式库**：`ToolRiskClassifier` + `TerminalCommandRisk`（`bash -c "rm -rf"`、`curl|sh`、`sudo`、`chmod 777`、`dd`/`mkfs`、fork 炸弹、覆盖重定向等命令级捕获）。
- **交互审批 UI**：`ToolApprovalDialog`（风险徽章、终端完整命令行、参数折叠、高危红色提示）+ 四档决策（拒绝 / 仅本次 / 本会话 / 永久）。
- **审计日志**：`ToolApprovalService` 对每次裁决 Serilog 记录（工具、风险、执行模式、决策、原因）。
- **配置与设置 UI**：`AppConfig` 增 `ToolApprovalMode`（默认 Balanced）/ `AutoAllowedTools` / `TerminalAllowlist` / `SubAgentsInheritApproval`；ConfigTab 新增「工具与安全」分区（模式下拉、子代理继承开关、永久放行清单与终端白名单的撤销）。
- **本地化**：en-US / zh-CN 补齐 `Dialog.ToolApproval.*` 与 `Config.ToolApproval.*` 键。
- **测试**：`Athena.Archive.Tests` 新增 7 项审批用例（分级、终端高危、只读免弹、无人值守拒绝、子代理继承、Trusted 放行、永久放行持久化）。

下文为原始设计方案，保留作为背景与决策记录。

---

## 0. 结论先行

当前**没有任何工具执行前的人工确认机制**。`FunctionRegistry.cs:326`、`FunctionRegistry.cs:151` 等描述里写的「Always confirm with the user」只是给模型的自然语言请求，**无代码强制力**；`execute_terminal_command` 更是把 FileSystem 工具辛苦搭起来的沙箱/黑名单一行绕过（详见评审第一条）。

本方案的核心是：**在工具真正执行前，插入一个可被 UI 拦截的审批闸门**，并配套：

1. **工具风险分级**（`ToolRiskClassifier`）——只读自动放行，写/破坏/外部副作用需审批。
2. **终端命令的命令级细粒度审批**——不是「要不要跑终端」，而是「要不要跑 `rm -rf ~`」。
3. **审批协调器 + 弹窗 UI**——复用现有 `ConfirmDialog` 模式，支持「本次允许 / 本会话始终允许 / 永久允许 / 拒绝」。
4. **无头路径（子代理 / 知识库维护）的默认策略**——无交互 owner 时按静态策略处理，不能卡死也不能裸奔。

---

## 1. 现状：工具是怎么被执行的

### 1.1 执行链路（唯一真源）

```
ChatTabViewModel.StreamMessageAsync(lambda onMessageAdded)
  └─ OpenAIChatService.StreamMessageAsync()          Services/OpenAIChatService.cs:100
       └─ ProcessStreamAsync()                        Services/OpenAIChatService.cs:142
            └─ foreach (toolCall in toolCalls)         :395  ← 审批闸门插这里
                 └─ ExecuteToolCallAsync()             :485
                      └─ _functionRegistry.ExecuteAsync(name, argsJson)   :492
```

关键代码（`Services/OpenAIChatService.cs:395-434`）：

```csharp
foreach (var toolCall in toolCalls)
{
    cancellationToken.ThrowIfCancellationRequested();
    Log.Information("执行工具: {Name} | 参数: {Args}", toolCall.FunctionName, toolCall.Arguments);
    using var toolConversationScope = _conversationSessionAccessor?.Enter(context.ConversationId);
    using var toolCancelScope = ToolExecutionContext.Enter(cancellationToken);
    var result = await ExecuteToolCallAsync(toolCall.FunctionName, toolCall.Arguments); // ← 无审批
    ...
    onMessageAdded?.Invoke(toolResultMsg);
}
```

**同一个 `FunctionRegistry.ExecuteAsync` 有三个调用方：**

| 调用方 | 文件 | 交互性 |
|---|---|---|
| 主对话循环 | `Services/OpenAIChatService.cs:492` | **有** UI，可弹窗 |
| 并发子代理 | `Services/SubAgents/SubAgentRunner.cs:153` | **无** UI（后台批量） |
| 知识库维护 | `Services/KnowledgeBaseMaintenanceRunner.cs:146` | **无** UI（定时后台） |

任何审批方案都必须同时覆盖这三条路径，否则「主对话拦住了，子代理照样 `rm -rf`」。

### 1.2 UI 通信现状：只有单向回调

`OpenAIChatService` 通过 `Action<ChatMessage>? onMessageAdded`（`:105`）**单向**通知 UI。审批需要的是**请求-应答**（问用户 → 等结果），现有回调不够，需要新增一个 `Func<..., Task<...>>` 形态的审批委托。

### 1.3 可复用的既有资产

- **`ConfirmDialog` + `ConfirmDialogViewModel`**：现成的模态确认弹窗，`ViewModels/ChatTabViewModel.cs:513` 已用于「会话回滚」，含「不再询问」并持久化到 config（`SkipRewindConfirm`）的完整范式。审批弹窗直接沿用这套。
- **`ToolExecutionContext`（AsyncLocal）**：`Services/SubAgents/ToolExecutionContext.cs` 已用 AsyncLocal 把取消令牌透传给工具。审批协调器可用同款模式做「环境审批器」注入。
- **`SubAgentToolGates`**：`Services/SubAgents/SubAgentToolGates.cs` 已把工具分成 `None/Browser/Write` 三类。风险分级器就是它的加强版，可对齐命名习惯。
- **`ToolCallEntry` + `ToolCallStatus`**：`Models/ToolCallEntry.cs` / `Models/ChatMessageSegment.cs:18`，工具卡片已有 `Running/Success/Failed` 状态与展开详情，天然适合加「待审批 / 已拒绝」态。
- **`ToolCallDisplay.Summarize/PrettyArguments`**：`Models/ToolCallDisplay.cs` 已能把工具调用整形成一行人话摘要 + 美化参数，审批弹窗直接拿来展示。

---

## 2. 目标设计

### 2.1 风险分级（三档 + 命令级例外）

新增 `Models/ToolRisk.cs`：

```csharp
public enum ToolRisk
{
    ReadOnly,    // 只读 / 无副作用：默认自动放行
    Sensitive,   // 写本地状态 / 外部副作用 / 花钱：默认每次询问
    Destructive  // 破坏性且不可逆：默认每次询问，弹窗红色高危样式
}
```

新增 `Services/ToolRiskClassifier.cs`（静态分类，单一真源，新增工具时在此登记）：

| 档位 | 工具 |
|---|---|
| **ReadOnly** | `read_system_file`、`get_file_info`、`search_in_file`、`list_system_directory`、`get_document_outline`、`recall_from_memory`、`view_self_configuration`、`web_search`、`list_tasks` |
| **Sensitive** | `write_system_file`、`modify_system_file`、`move_system_file`、`copy_system_file`、`create_directory`、`create_new_memory`、`modify_self_configuration`、`run_browser_task`、`generate_image`、`create_task`、`cancel_task`、`parse_office_document`、`dispatch_subagents` |
| **Destructive** | `delete_system_file`、`execute_terminal_command`（默认，见下） |

> 设计原则：**未登记的工具默认 `Sensitive`**（fail-safe，新工具默认要审批，而非默认放行）。

### 2.2 终端命令的命令级细粒度

`execute_terminal_command` 一律按 `Destructive` 弹窗还嫌粗——`git status`、`node -v` 也弹会烦死人。方案：

- 在 `CliFunctions` 侧或 classifier 侧，对 `command` + `arguments` 做**命令级评估**：
  - **明确高危模式**（`rm`/`del`、`sudo`、`curl|sh`、`bash -c`、`chmod 777`、重定向覆盖 `>` 到系统路径等）→ `Destructive`，红色弹窗，且默认**不允许**「本会话始终允许」。
  - **常见只读命令**（`git status/log/diff`、`ls`、`cat`、`*-v/--version`、`echo`、`pwd`）→ 可降级为 `Sensitive` 甚至列入用户可配置白名单后自动放行。
  - 其余 → `Sensitive`。
- 审批弹窗对终端命令**展示完整拼接后的命令行**（`command + " " + string.Join(' ', arguments)`），让用户看清到底要跑什么，而不是只看到函数名。

> 这一步同时把评审第一条的「终端零校验」补上：审批弹窗本身就是终端的执行前闸门。

### 2.3 审批决策模型

新增 `Models/ToolApprovalDecision.cs`：

```csharp
public enum ToolApprovalScope
{
    Deny,           // 拒绝本次
    AllowOnce,      // 仅本次
    AllowForSession,// 本会话内该工具（或该命令）不再询问
    AllowAlways     // 永久放行该工具 → 写入 config
}
```

请求对象 `ToolApprovalRequest`：工具名、`ToolRisk`、人类可读摘要（`ToolCallDisplay.Summarize`）、美化参数、（终端专用）完整命令行、ToolCallId。

### 2.4 审批策略（config）

在 `Models/AppConfig.cs` 新增（沿用 `[ObservableProperty]` 范式）：

```csharp
[ObservableProperty] private ToolApprovalMode _toolApprovalMode = ToolApprovalMode.Balanced;
// Off       = 全部自动放行（老行为，给完全信任的用户）
// Balanced  = ReadOnly 放行，Sensitive/Destructive 询问（默认，推荐）
// Strict    = 全部工具都询问（含只读）
[ObservableProperty] private List<string> _autoAllowedTools = new();   // 用户勾了「永久允许」的工具名
[ObservableProperty] private List<string> _terminalAllowlist = new();  // 用户信任的终端命令前缀
[ObservableProperty] private bool _subAgentsInheritApproval = true;    // 子代理是否也走审批（无 UI 时按 2.7 降级）
```

> 默认 `Balanced`：**开箱即安全**，只读不打扰、写/删/终端必问。这与评审「安全债必须先还」的优先级一致。

### 2.5 审批协调器（UI 侧）

新增 `Services/Interfaces/IToolApprovalCoordinator.cs` + 实现 `Services/ToolApprovalService.cs`：

```csharp
public interface IToolApprovalCoordinator
{
    // 由 chat 服务在执行前调用；返回是否放行。实现内部决定是否弹窗。
    Task<ToolApprovalDecision> RequestAsync(ToolApprovalRequest request, CancellationToken ct);
}
```

`ToolApprovalService` 职责：
1. 读 `AppConfig` 判定档位 / 白名单 / 已永久放行 → 命中则直接返回 `AllowOnce`，**不弹窗**。
2. 查本会话内存态 session-allow 集合 → 命中直接放行。
3. 未命中 → 在 **UI 线程**（`Dispatcher.UIThread`）弹 `ToolApprovalDialog`，`await ShowDialog(owner)`。
4. 用户选 `AllowForSession` → 记入会话集合；选 `AllowAlways` → 追加进 `config.AutoAllowedTools` 并 `SaveAsync`。
5. 尊重 `CancellationToken`（用户点了「停止」时不应卡在弹窗上）。

### 2.6 把闸门接进执行循环

**推荐做法：闸门放在 `OpenAIChatService.ProcessStreamAsync` 循环内**（`:395` 前），通过新增委托参数注入，理由是这里能拿到 `cancellationToken`、能区分交互/无头调用方、可测试。

新增贯穿签名的委托（`StreamMessageAsync` → `ProcessStreamAsync`）：

```csharp
Func<ToolApprovalRequest, CancellationToken, Task<ToolApprovalDecision>>? onToolApprovalRequested = null
```

循环内改造：

```csharp
foreach (var toolCall in toolCalls)
{
    cancellationToken.ThrowIfCancellationRequested();

    // —— 审批闸门 ——
    if (onToolApprovalRequested != null)
    {
        var risk = ToolRiskClassifier.Classify(toolCall.FunctionName, toolCall.Arguments);
        var req  = ToolApprovalRequest.From(toolCall, risk);
        var decision = await onToolApprovalRequested(req, cancellationToken);
        if (!decision.Approved)
        {
            // 关键：被拒也要回填一条 tool 结果，否则 OpenAI 协议下"有 tool_call 无 tool 结果"会让下一轮请求 400
            var denied = FunctionResult.FailureResult("用户拒绝了该工具调用。请改用其他方式或向用户说明。");
            var deniedJson = denied.ToJson();
            onMessageAdded?.Invoke(new Models.ChatMessage {
                Role = "tool", Content = deniedJson,
                ToolCallId = toolCall.Id, ToolName = toolCall.FunctionName, Timestamp = DateTime.Now });
            messages.Add(new ToolChatMessage(toolCall.Id, deniedJson));
            context.AddToolMessage(deniedJson, toolCall.Id);
            continue;   // 不执行，进入下一个 toolCall
        }
    }

    using var toolConversationScope = _conversationSessionAccessor?.Enter(context.ConversationId);
    using var toolCancelScope = ToolExecutionContext.Enter(cancellationToken);
    var result = await ExecuteToolCallAsync(toolCall.FunctionName, toolCall.Arguments);
    ...
}
```

> ⚠️ **协议正确性**（务必）：OpenAI 工具协议要求每个 `tool_call` 都要有对应的 `tool` 结果消息。被拒绝时**必须回填一条「用户已拒绝」的 tool 结果**（如上），否则下一轮 `messages` 校验失败。这也让模型知道「用户不同意」从而换策略，而不是重复调用。

### 2.7 无头路径（子代理 / 维护）的降级策略

`SubAgentRunner` / `KnowledgeBaseMaintenanceRunner` 无交互 owner，不能弹窗。策略：

- 给这两条路径注入一个**非交互审批器** `NonInteractiveApprover`，按静态策略裁决：
  - `ReadOnly` → 放行；
  - `Sensitive` → 若 `config.SubAgentsInheritApproval == true`，则按「已永久放行清单 / 白名单」放行，否则**拒绝并把拒绝原因回给子代理**（子代理据此在 summary 里说明「因权限被拒未能完成 X」）；
  - `Destructive`（尤其终端 `rm`、`delete_system_file`）→ **默认拒绝**。子代理不应在无人看管时删文件 / 跑高危命令。
- 这样并发子代理仍能干只读调研、生图等活，但破坏性操作被结构性挡住——与评审「子代理烧钱 + 无边界」担忧对齐。

> 备选（更强、后续演进）：把审批闸门下沉到 `FunctionRegistry.ExecuteAsync` 这个**唯一 chokepoint**，用 AsyncLocal 环境审批器（仿 `ToolExecutionContext`）注入。好处是三条路径无法绕过、无需改三处签名；代价是 registry 从「纯执行」变成「带策略」，且 async 流下 AsyncLocal 传播需小心。**建议第一期先用 2.6 的显式委托方案（清晰、易测），第二期评估是否下沉。**

---

## 3. UI 落地

### 3.1 审批弹窗 `Views/ToolApprovalDialog.axaml`

新建，结构参照 `Views/ConfirmDialog.axaml`（Semi 主题，见项目记忆 `semi-avalonia-textbox-theming`）：

```
┌────────────────────────────────────────┐
│  🛡️  Athena 请求执行工具                  │   ← 高危时标题/图标转红
├────────────────────────────────────────┤
│  工具：delete_system_file  [破坏性]        │   ← Risk 徽章（绿/黄/红）
│  摘要：删除文件 report_old.md              │   ← ToolCallDisplay.Summarize
│  ┌── 参数（可折叠）─────────────────┐      │
│  │ path: /Users/.../report_old.md   │      │   ← PrettyArguments
│  │ recursive: false                 │      │
│  └──────────────────────────────────┘      │
│  ⚠ 此操作不可逆                            │   ← Destructive 时显示
├────────────────────────────────────────┤
│  [拒绝]  [仅本次允许]  [本会话允许 ▾]      │
│         └ ▾ 展开：□ 永久允许此工具         │
└────────────────────────────────────────┘
```

- **终端命令**特化：摘要区改为等宽字体展示完整命令行 `$ rm -rf ~/tmp`，让用户看清真实命令。
- **风险徽章**：`ReadOnly` 绿、`Sensitive` 黄、`Destructive` 红（复用 `Converters/` 里现有的状态色转换器或新增一个 `RiskToBrushConverter`）。
- 对应 `ViewModels/ToolApprovalDialogViewModel.cs`：暴露 `ToolName / RiskLabel / Summary / PrettyArgs / CommandLine / IsDestructive / IsTerminal`，四个 `[RelayCommand]`（Deny/AllowOnce/AllowSession/AllowAlways），产出 `ToolApprovalDecision`。沿用 `ConfirmDialogViewModel` 的 `RequestClose` 关闭范式。

### 3.2 工具卡片状态扩展

`Models/ChatMessageSegment.cs:18` 的 `ToolCallStatus` 增加：

```csharp
public enum ToolCallStatus { Running, Success, Failed, AwaitingApproval, Denied }
```

- `AwaitingApproval`：弹窗待用户决策时，卡片显示「⏸ 等待授权…」。
- `Denied`：用户拒绝后卡片显示「🚫 已被你拒绝」灰态。
- 需同步更新 `ToolCallEntry.cs` 的派生布尔（`IsRunning` 等）与 `Views/ToolCallStackView` 的样式触发器、`ToolCallDisplay` 的状态文案。

### 3.3 ChatTabViewModel 接线

`ViewModels/ChatTabViewModel.cs:1440` 调用 `StreamMessageAsync` 处，新增 `onToolApprovalRequested` 实参，转调注入的 `IToolApprovalCoordinator`：

```csharp
onToolApprovalRequested: async (req, ct) =>
{
    // 若开了对应工具卡片，可先把卡片状态置 AwaitingApproval（通过 ToolCallId 匹配）
    var decision = await _toolApprovalCoordinator.RequestAsync(req, ct);
    if (!decision.Approved) MarkToolCallDenied(req.ToolCallId);
    return decision;
},
```

`IToolApprovalCoordinator` 经构造函数注入（DI 注册在 `App.axaml.cs`，与其它服务一致）。协调器需要拿主窗口做 owner——复用 `ChatTabViewModel.GetMainWindow()`（`:763`）同款逻辑，或由协调器自行解析 `ApplicationLifetime.MainWindow`。

### 3.4 设置页 `Views/ConfigTab` 新增「工具与安全」分区

- **审批模式**下拉：关闭 / 均衡（默认）/ 严格。
- **已永久放行的工具**列表 + 逐项「撤销」按钮（读写 `config.AutoAllowedTools`）。
- **终端命令白名单**可编辑列表（`config.TerminalAllowlist`）。
- **子代理继承审批**开关（`config.SubAgentsInheritApproval`）。
- 文案走本地化：`Assets/Locales/Locale.en-US.axaml` / `Locale.zh-CN.axaml` 补 `Config.ToolApproval.*` 与 `Dialog.ToolApproval.*` 键，AXAML 用 `{loc:Loc ...}`（遵循 CLAUDE.md 本地化约定）。

---

## 4. 分期实施计划

### 第一期（P0，安全债，1–2 天）——最小可用闸门
1. `Models/ToolRisk.cs` + `Services/ToolRiskClassifier.cs`（含终端命令级评估）。
2. `Models/ToolApprovalRequest.cs` / `ToolApprovalDecision.cs`。
3. `OpenAIChatService` 加 `onToolApprovalRequested` 委托并接入循环（含**被拒回填 tool 结果**）。
4. `IToolApprovalCoordinator` + `ToolApprovalService`（先只支持 AllowOnce/Deny，弹窗复用 ConfirmDialog 也可）。
5. `AppConfig` 加 `ToolApprovalMode`（默认 `Balanced`）。
6. 无头路径（子代理 / 维护）注入 `NonInteractiveApprover`，破坏性默认拒绝。

**验收**：`delete_system_file`、`execute_terminal_command`、`write/modify_system_file` 执行前必弹窗；只读工具不弹；拒绝后对话不崩、模型收到「已拒绝」。

### 第二期（P1，体验，1–2 天）
7. 专用 `ToolApprovalDialog` + `ViewModel`（风险徽章、参数折叠、终端命令行展示、四档决策）。
8. `ToolCallStatus` 加 `AwaitingApproval/Denied` 及卡片样式。
9. session-allow + `AllowAlways` 持久化，`AutoAllowedTools` / `TerminalAllowlist` 生效。
10. 设置页「工具与安全」分区 + i18n。

### 第三期（P2，加固，可选）
11. 评估把闸门下沉到 `FunctionRegistry.ExecuteAsync` chokepoint（AsyncLocal 环境审批器），彻底堵住任何绕过。
12. 审批审计日志（Serilog 记录每次决策：谁、放行/拒绝、工具、参数摘要），便于事后追溯。
13. 终端命令高危模式库持续完善（`curl|sh`、`sudo`、`chmod 777`、覆盖重定向等）。

---

## 5. 风险与注意事项

| 风险 | 说明 | 对策 |
|---|---|---|
| **协议 400** | 被拒的 tool_call 无对应 tool 结果 → 下一轮请求报错 | 2.6 强制回填「已拒绝」tool 结果 |
| **弹窗风暴** | 一轮多个 toolCall 逐个弹窗，用户疲劳 | 均衡档只读不弹；支持「本会话始终允许」；可选后续做「本轮批量审批」聚合弹窗 |
| **UI 线程死锁** | 服务在后台线程 `await` 弹窗 | 协调器内 `Dispatcher.UIThread.InvokeAsync` 弹窗，服务侧只 await Task |
| **取消卡死** | 用户点「停止」时弹窗仍挂着 | `RequestAsync` 尊重 `CancellationToken`，取消即关弹窗返回 Deny |
| **无头裸奔** | 子代理/维护绕过审批 | 2.7 非交互审批器，破坏性默认拒绝 |
| **老用户行为突变** | 突然处处弹窗 | 提供 `Off` 档；首次触发时可给一次性说明；默认 `Balanced` 只拦写/删/终端 |

---

## 6. 涉及改动文件清单

**新增**
- `Models/ToolRisk.cs`
- `Models/ToolApprovalRequest.cs`
- `Models/ToolApprovalDecision.cs`（含 `ToolApprovalScope` / `ToolApprovalMode` 枚举，或拆分）
- `Services/ToolRiskClassifier.cs`
- `Services/Interfaces/IToolApprovalCoordinator.cs`
- `Services/ToolApprovalService.cs`
- `Services/NonInteractiveApprover.cs`（无头路径）
- `Views/ToolApprovalDialog.axaml` (+ `.axaml.cs`)
- `ViewModels/ToolApprovalDialogViewModel.cs`
- `Converters/RiskToBrushConverter.cs`（可选）

**修改**
- `Services/OpenAIChatService.cs`：`StreamMessageAsync` / `ProcessStreamAsync` 加委托参数，循环内接闸门 + 被拒回填。
- `Services/SubAgents/SubAgentRunner.cs`、`Services/KnowledgeBaseMaintenanceRunner.cs`：接非交互审批器。
- `Services/Interfaces/IChatService.cs`：同步接口签名。
- `Models/AppConfig.cs`：新增审批相关配置项。
- `Models/ChatMessageSegment.cs`（`ToolCallStatus`）、`Models/ToolCallEntry.cs`、`Models/ToolCallDisplay.cs`：新增状态。
- `ViewModels/ChatTabViewModel.cs`：注入协调器、接线 `onToolApprovalRequested`、卡片置态。
- `ViewModels/ConfigTabViewModel.cs` + `Views/ConfigTab*`：设置分区。
- `App.axaml.cs`：DI 注册 `IToolApprovalCoordinator`。
- `Assets/Locales/Locale.en-US.axaml` / `Locale.zh-CN.axaml`：i18n 键。
- `Athena.Archive.Tests`：为 classifier / 被拒回填 / 无头降级补单测。

---

## 7. 与产品评审的对齐

本方案直接兑现 `ProductReview_CN.md` 优先级第 1 项「工具审批闸门 + 终端安全边界」：

- **工具权限分级** → §2.1 `ToolRiskClassifier`（自动允许 / 每次询问 / 默认拒绝）。
- **破坏性操作强制弹窗** → §2.6 闸门 + §3.1 高危红色弹窗，代码级强制，不再依赖模型「自觉」。
- **终端命令白名单或审批** → §2.2 命令级评估 + §2.4 `TerminalAllowlist`。
- **子代理无边界隐患** → §2.7 无头降级，破坏性默认拒绝。

> 一句话：把散落在工具描述里、无强制力的「Always confirm with the user」，变成一道**代码级、可配置、覆盖全部三条执行路径**的审批闸门。
