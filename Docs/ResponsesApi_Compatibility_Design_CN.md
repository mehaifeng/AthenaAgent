# Athena「Responses API 兼容」详细设计

> 文档状态：P0–P4 全部实施完成  
> 版本：1.2  
> 日期：2026-08-07  
> 适用项目：Athena.UI（.NET 10 / Avalonia / OpenAI SDK 2.12.0）  
> 主要读者：产品、UI、架构、实现与测试人员，以及接手实施的新 Codex 会话

## 0. 新会话接手指南

这份文档是本需求的实施真源。新会话无需回看原始讨论即可开始，但必须先完成以下检查：

- [x] 阅读仓库根目录 `CLAUDE.md` 与 `AGENTS.md`。
- [x] 运行 `git status --short`，保留用户已有改动，不覆盖无关文件。
- [x] 阅读本文第 1、3、5、6、7、11 节，再动手。
- [x] 先完成 Phase 0（协议配置管道）和回归测试，再做传输层改造。
- [x] 每完成一个阶段，只勾选已经通过该阶段验收标准的条目。
- [x] 若实现与本文发生偏离，在本文「决策变更记录」中写明原因、替代方案和迁移影响。

已知的参考资料（外部）：
- OpenAI 官方「Responses API」文档（开放规范：openresponses.org）。
- SDK 源码本地镜像 `D:\MySources\openai-dotnet`（2.12.0 之后的 main 分支，含 CHANGELOG、TypeSpec 规范与测试）。本文所有 SDK 类型名均以该镜像为准，与 NuGet 包 `OpenAI 2.12.0` 一致（Responses 类型随主包发布，无需新增包引用）。

## 1. 已确认的产品决策

以下规则已在业务讨论中确定，不应在实现阶段自行改写：

- [ ] **不替代 Chat Completions**。Chat Completions 保持为默认与兜底传输；Responses 是"第二种可选传输"，按 provider 配置。
- [ ] **协议配置放在 provider 级**（`OpenAiProviderConfiguration`），三档：`Auto`（默认）/ `ChatCompletions` / `Responses`。不放全局、不放角色级。
- [ ] **默认 Auto（被动生效）**，但 Auto 的判定必须保守：只在「能确认支持」或「官方端点 + 推理模型」时才切到 Responses；未知/手动 provider 一律走 Chat Completions。
- [ ] **无状态模式**：Responses 传输一律 `store: false`，每次请求全量重发 input items，**不使用 `previous_response_id` 服务端记账**——与 Athena 自有的上下文管理（压缩、回缩、分支、归档）互斥。
- [ ] **工具全部走自有 `FunctionRegistry.ExecuteAsync` 审批闸门**，不使用 Responses 内置工具（web_search / file_search / code_interpreter 等）——内置工具绕过审批/审计，且第三方端点基本不支持。
- [ ] **失败自动降级**：已判定/强制为 Responses 的请求若遇到「端点不支持」类错误（404/405 或协议级错误），自动降级为 Chat Completions 重发一次，`Log.Warning` 并提示用户，不硬失败。
- [ ] **协议进运行时身份**：`OpenAiModelClientIdentity` 与 `OpenAiModelExecutionPolicyIdentity` 均纳入协议维度；协议变化触发客户端重建与压缩/校准缓存键隔离。
- [ ] **校准按传输分桶**：`ITokenCalibrationService` 的观察特征（Features）携带传输标识，避免跨协议 usage 信号污染。
- [ ] **连接测试保持 Chat Completions 恒定**（`OpenAIChatService.TestConnectionAsync` 与 `OpenAiModelRuntimeFactory.TestChatAsync`）：测试目标是「连接与密钥有效」，不随协议切换，避免端点探测歧义。
- [ ] **推理文本双通道**：Responses 传输下用 `include: ["reasoning"]` 获取完整推理文本，归一化为与 `reasoning_content` 相同的 `ReasoningContent` 内部字段；请求侧回放规则按传输区分（chat=回放以兼容 DeepSeek 系；responses=不回放，见 §7.4）。
- [ ] **非推理模型不切 Responses**：协议收益集中在推理模型（官方 3% SWE-bench、缓存收益、推理文本可见性），非推理模型切协议无意义且引入风险。

## 2. 背景、目标与非目标

### 2.1 背景

- OpenAI 明确表示 Chat Completions 作为行业标准无限期支持，但新能力（推理文本可见、server-side state、内置工具、程序化工具调用）只在 Responses API 提供；推理模型（gpt-5 系列等）走 Responses 有官方声明的收益：SWE-bench +3%（同提示词同设置）、缓存命中率 +40–80%。
- 生态端，OpenRouter、阿里云百炼（Qwen）、NVIDIA NeMo Gym、LM Studio 等已提供 `/v1/responses` 兼容端点；OpenAI 发布了开放规范 openresponses.org。但 DeepSeek 原生 API、Ollama 等仍以 chat completions 为主——双协议并存是未来一段时间的常态。
- Athena 目前全部走 Chat Completions：主对话流式环（`OpenAIChatService.ProcessStreamAsync`）+ 15 个非流式调用点。思考文本只能靠 JSON Patch 偷读第三方扩展字段 `$.choices[0].delta.reasoning_content`，官方直连模型永远拿不到思考过程。

### 2.2 目标

1. 支持按 provider 切换 Chat Completions / Responses 双传输，默认 Auto。
2. 主对话在 Responses 下完整可用：流式输出、工具循环（含审批闸门）、压缩/校准/重试、图片降级、推理文本展示与持久化。
3. 用到 Responses 的「全部有效能力」：推理文本（完整文本 + 摘要）、reasoning effort / summary 详略控制、无状态工具循环。明确不用的能力见 §2.4。
4. 非流式调用点（标题、压缩、审批、子代理等）在同一 provider 下自动跟随协议，语义等价。

### 2.3 非目标

- 不迁移/不替换 Chat Completions 主路径。
- 不使用 Responses 内置工具（web_search、file_search、code_interpreter、computer_use）。
- 不使用服务端状态（store、previous_response_id、server-side conversation）。
- 不做协议探测（启动时发探针请求判断端点是否支持 /responses）——由目录元数据 + 显式配置决定，失败降级兜底。
- 不引入新的 NuGet 依赖：Responses 类型已随 `OpenAI 2.12.0` 主包发布（`OpenAI.csproj` 内联编译 `OpenAI.Responses` 源树，命名空间 `OpenAI.Responses`）。

### 2.4 能力取舍矩阵

| Responses 能力 | 是否采用 | 理由 |
|---|---|---|
| 推理文本（流式 `response.reasoning_text.delta` / 摘要事件） | ✅ 采用 | 官方模型思考文本的唯一路径 |
| reasoning effort（`reasoning.effort`）与 summary 详略 | ✅ 采用 | 配置管道透传 |
| 无状态工具循环（function_call / function_call_output items） | ✅ 采用 | 必须；语义等价于现有循环 |
| `store: true` / `previous_response_id` 服务端记账 | ❌ 不采用 | 与 Athena 自有上下文管理互斥 |
| 内置工具（web_search / file_search / code_interpreter / computer_use / MCP） | ❌ 不采用 | 绕过 `FunctionRegistry.ExecuteAsync` 审批闸门；第三方端点不支持 |
| background mode、Compaction API、GetResponse 回放 | ⏸ 暂不采用 | 无明确需求，不阻塞 |

## 3. 现状盘点（传输依赖点全景）

### 3.1 内部模型层：传输无关，无需改动 ✅

- `ConversationContext` / `ContextMessage`（`Models/ConversationContext.cs`）：Role / Content / ToolCallId / ToolCallsJson / ReasoningContent / OutputAudioReferenceId / Attachments——纯内部形状。
- `Models/ChatMessage`（VM 层）同样传输无关；`ConversationPersistenceHelper` 已持久化 `ReasoningContent`。
- `TokenService` / `TokenUsageSnapshot`（`Services/TokenService.cs`）：Input/Cached/Output/Total 四元组，传输无关。
- `ITokenCalibrationService`、压缩管线（Planner/CandidateGenerator/Validator）、`ContextRequestPreparer`：消费内部消息与 usage 形状（`ContextRequestPreparer` 通过 `AssistantChatMessage.Patch` 读 `$.reasoning_content` 估 token——该读取点在 Chat 对象上，见 §7.4 风险）。

### 3.2 主对话流式环（唯一复杂点）

`Services/OpenAIChatService.cs`（2392 行）结构：

- **入口** `StreamMessageAsync`（168–323）：快照创建 → `BuildMessages` → `ProcessStreamAsync` 迭代；图片拒绝降级重试（265–320，`TryDescribeImagesAsync` + `isImageFallback` 重发）。
- **快照** `CreateRequestRuntimeSnapshotAsync`（325–476）：解析 provider/模型/元数据/策略，构造 `EffectiveRequestRuntimeSnapshot`（持有 `ChatClient` + `ChatCompletionOptions` + `ChatTool[]` + 系统提示等）。
- **主环** `ProcessStreamAsync`（513–1031）：
  - 每轮：`ContextRequestPreparer.Prepare`（特征/指纹）→ 压缩触发判断（事务压缩）→ `runtime.ChatClient.CompleteChatStreamingAsync(messages, options)` → 流消费。
  - 流消费（714–795）：usage 上报回调 → `AppendReasoningContent`（JSON Patch 读 `reasoning_content`）→ 文本增量 → 工具调用增量（按 index 累积 `ToolCallBuilder`）→ finishReason。
  - 工具轮：截断检测（finishReason==Length **或** 参数 JSON 不完整，837–854）→ 重试指令（`rebuildTail`）→ `context.AddAssistantMessage(...)` → 逐工具执行（`FunctionRegistry.ExecuteAsync`，`ToolApprovalContext.EnterInteractive()`）→ `ToolChatMessage` 回填。
  - 中断修复（991–1027）：未完成工具调用补齐「已中断」tool 结果。
- **请求组装** `BuildMessagesCore`（1249–1347）：合并 system（persona + 历史摘要信封 + MCP 目录 + Skill 目录 + 工作区知识）→ 逐消息映射（user 含图片二进制 / assistant 含 reasoning patch + tool_calls / tool）→ `SanitizeToolCallPairing`（清理失配的 tool_calls/tool 对，防 400 "insufficient tool messages"）。
- **JSON Patch 三处**：读 `reasoning_content`（1053–1062）、写回 `$.reasoning_content`（1064–1074）、读 usage 明细 `image_tokens`/`text_tokens`（1104–1115）——全部是 Chat Completions 特有（官方/第三方 chat 端点的扩展字段），Responses 有原生等价物。

### 3.3 非流式调用点清单（15 个，全为非流式 `CompleteChatAsync`）

| # | 文件:行号 | 角色 | 消息/选项要点 | 响应读取 | 备注 |
|---|---|---|---|---|---|
| 1 | `SubAgents/SubAgentRunner.cs:116` | 子代理 | system+user，后续轮 assistant/tool 回填；Tools=白名单 | `ToolCalls` 优先，无则 `Content[0].Text` | 逐轮循环（最多 N 轮）；`EnterNonInteractive()` |
| 2 | `ConversationTitleGenerator.cs:65` | 标题 | system+历史+指令；Temperature 0.2 | `Content.FirstOrDefault()?.Text` | 静默降级 |
| 3 | `CommitMessageGenerator.cs:51` | 提交信息 | 复用主对话角色；Temp 0.3 / Max 200 | `Content.FirstOrDefault()?.Text` | |
| 4 | `KnowledgeBaseMaintenanceRunner.cs:118` | KB 维护 | 循环回填 assistant/tool；Tools=白名单 | `ToolCalls` 优先，无则 `Content[0].Text` | `EnterTrusted()` |
| 5 | `AiToolApprovalEvaluator.cs:58` | 审批 | Temp 0 / Max 64..512 / `CreateJsonObjectFormat()` | `Content[0].Text` → JSON 解析 decision/reason | fail-closed |
| 6 | `ContextCompressionService.cs:68` | 压缩 | 历史渲染为文本进 prompt（含 ReasoningContent） | `Content.FirstOrDefault()?.Text` | |
| 7 | `ContextCompressionService.cs:180` | 工作区知识压缩 | 同压缩角色 | `Content.FirstOrDefault()?.Text` | |
| 8 | `Context/OpenAiCompressionTextGenerator.cs:42` | 压缩文本 | 同压缩角色 | `string.Concat(Content.Select(Text))` | |
| 9 | `Browser/BrowserTaskPlanner.cs:54`→`BrowserStructuredOutput.cs:47/61` | 浏览器规划 | Temp 0 / Max 800..2000 / json_object 协商（Auto 档 400/404/422 降级重试） | `Content.FirstOrDefault()?.Text` → JSON | |
| 10 | `Browser/BrowserVisionService.cs:72`→同上 | 浏览器决策 | 条件多模态（截图 image part，Low） | `Content.FirstOrDefault()?.Text` → JSON | |
| 11 | `Browser/BrowserVisionService.cs:145`→同上 | SoM 视觉决策 | 恒多模态（截图 image part，Low） | `Content.FirstOrDefault()?.Text` → JSON | |
| 12 | `Browser/BrowserVisionService.cs:214` | 浏览器连接测试 | 探针 64×64 PNG（非 DOM-only 时）；只设 Max 512；依赖 `FinishReason==Length` 诊断空响应 | `Content.FirstOrDefault()?.Text` | 不设 Temperature（部分供应商拒收显式参数） |
| 13 | `OpenAiModelRuntimeFactory.cs:193` | 连接测试 | 单 user 消息 + 空选项 | `string.Concat(Content.Select(Text))` | |
| 14 | `OpenAIChatService.cs:1778` | 图片识别降级 | 恒多模态（Auto detail）；Temp 0.1 / Max 4096 | `string.Concat(Content.Select(Text))` | 失败静默 |
| 15 | `OpenAIChatService.cs:1904` | 主对话连接测试 | Temp/TopP/Max 10 + 全部工具注入或 ToolChoice=None | `Content[0].Text` | 见 §1 决策：恒定 Chat Completions |

要点：**没有任何调用点读 usage；响应侧无 reasoning_content 依赖**；唯一输入侧 reasoning 消费在 `ContextCompressionService.FormatMessage`（把历史 ReasoningContent 渲染成文本）。多模态点 3 处（#10/#11/#14）+ 探针 1 处（#12）。json_object 协商在 2 处（#5/#9–11）。

### 3.4 配置 / UI / 持久化现状

- Provider 编辑在 `Views/ProviderModelsWindow.axaml`「连接」Tab（103–220 行），绑定 `SelectedProvider.*`；无保存按钮，`AppConfigurationSession.TrackAiModels`（`Services/AppConfigurationSession.cs:143–155`）500ms 防抖自动保存。
- 保存后 `AppConfigurationApplier`（`Services/AppConfigurationApplier.cs:69–118`）用 `OpenAiModelRuntimeFactory.ComputeClientIdentity`（`OpenAiModelRuntimeFactory.cs:63–78`，record `OpenAiModelClientIdentity` 213–217）比较身份；**身份变化才调用 `_chatService.UpdateConfig(config)` 重建客户端**——协议字段必须进身份，否则切协议不重建。
- 执行策略指纹 `OpenAiModelExecutionPolicyIdentity`（`OpenAiModelRuntimeFactory.cs:219–226`）：ProviderId/ExternalModelId/ProfileRevision/CatalogRevision/ContextWindow/MaxOutput/RequestFormatVersion——压缩/校准缓存键的语义身份；协议变化必须让这个身份变化。
- 元数据覆盖链路：`ModelMetadataOverrides`（`Models/AiModelConfiguration.cs:55–72`，含 `SupportsReasoning`）→ `ModelMetadataResolver.ResolveCapability`（`Services/ModelMetadata/ModelMetadataResolver.cs:59`，OpenRouter `SupportedParameters` 里取 "reasoning"）→ `ResolvedModelMetadata`（`Models/ModelMetadataModels.cs:100–112`）→ `ProviderModelMetadataItemViewModel` 展示/覆盖。
- `ConfigService`（`Services/ConfigService.cs`）：`config.json`，CamelCase，**无 JsonStringEnumConverter**（枚举存数字）；缺失字段靠属性初始化器兜底，新增枚举字段无需迁移（schema 版本 5/6 分支不动）。

### 3.5 测试现状

`Athena.UI.HeadlessTests/Program.cs`（5054 行）：

- 主对话流式环直接覆盖 5 例（`TestImmediateToolCallUsageAsync` 2942–3007、`TestAutomaticCompressionFailureBudgetBehaviorAsync` 3038–3110、`TestSameRevisionNotCompressibleCacheAsync` 3112–3171、`TestToolLoopTransactionalCompressionAsync` 3173–3250、`TestDeletedWorkspacePolicyFallbackAsync` 2780–2814）。
- 桩方式统一：真 `OpenAIClient` + `HttpClientPipelineTransport` + 假 SSE handler（`text/event-stream`，输出 `chat.completion.chunk` 格式）+ reflection `FieldInfo.SetValue` 注入 `OpenAIChatService._chatClient`（5 处：2796/2981/3075/3151/3216）。
- 假管道 3 个：`ToolLoopSseHandler`（4731–4762）、`FinalOnlySseHandler`（4764–4786）、`TruncatedThenFinalSseHandler`（4788–4815，首轮截断 tool 参数触发重试）。
- **空白**：reasoning_content 解析（传输层）零覆盖；图片降级端到端零覆盖；审批闸门与流式工具循环的集成零覆盖。
- `Athena.Archive.Tests`：ReasoningContent 的压缩/计划/校准特征覆盖已有（1961–2059 等），传输无关，无需改动。

## 4. OpenAI SDK Responses API 能力面（2.12.0 已内置）

以下全部类型位于 `OpenAI.Responses` 命名空间，随 `OpenAI 2.12.0` 包发布（`OpenAI.csproj` 内联编译 `OpenAI.Responses/src`），**均标注 `[Experimental("OPENAI001")]`**（编译警告，处理方式同现有 `pragma warning disable OPENAI001`）。

### 4.1 客户端与端点

```csharp
var options = new ResponsesClientOptions
{
    Endpoint = new Uri(baseUrl.Trim()),          // 与 OpenAIClientOptions.Endpoint 同语义
    RetryPolicy = new ClientRetryPolicy(3),
    NetworkTimeout = TimeSpan.FromSeconds(timeout),
};
var responses = new ResponsesClient(new ApiKeyCredential(apiKey), options);
```

- 端点结论：`ResponsesClient` 请求打到 `{Endpoint}/responses`（SDK 内部 `GetEndpoint` 归一化，README Azure 示例为 `{endpoint}/openai/v1/`）。**与 chat 兼容端点共用 BaseUrl 的 provider，/responses 路径即 `BaseUrl + "/responses"`**——这是降级判断（404/405）的直接依据。
- 方法面：`CreateResponseAsync(CreateResponseOptions)`（非流式）、`CreateResponseStreamingAsync(CreateResponseOptions)`（流式，`AsyncCollectionResult<StreamingResponseUpdate>` 可 `await foreach`）；流式必须先设 `StreamingEnabled = true`，SDK 会校验。

### 4.2 CreateResponseOptions 关键属性

`Model`、`InputItems`（`IList<ResponseItem>`）、`Instructions`（string，system 提示位）、`Tools`（`IList<ResponseTool>`）、`ReasoningOptions`（`ResponseReasoningOptions`）、`IncludedProperties`（`IList<IncludedResponseProperty>`，即 `include`）、`StreamingEnabled`、`MaxOutputTokenCount`、`Temperature`、`TopP`、`PreviousResponseId`、`StoredOutputEnabled`（即 `store`，**恒 false**）、`Metadata`、`TextOptions`（`ResponseTextOptions`）。

### 4.3 ResponseItem 输入构造（静态工厂）

| 语义 | 工厂 |
|---|---|
| user 消息（文本 / parts） | `ResponseItem.CreateUserMessageItem(string)` / `(IEnumerable<ResponseContentPart>)` |
| system / developer | `CreateSystemMessageItem(...)` / `CreateDeveloperMessageItem(...)` |
| assistant 历史（纯文本） | `CreateAssistantMessageItem(string)` |
| assistant 历史（含工具调用） | `CreateFunctionCallItem(callId, name, BinaryData args)`（与消息同层级平铺） |
| 工具结果 | `CreateFunctionCallOutputItem(callId, output)` |
| 推理历史（若回放） | `CreateReasoningItem(string summaryText)` |
| 图片 part | `ResponseContentPart.CreateInputImagePart(BinaryData bytes, ResponseImageDetailLevel?)` |
| 文本 part | `ResponseContentPart.CreateInputTextPart(string)` |

### 4.4 流式事件全集（`StreamingResponseUpdate` 子类，按 Kind 分发）

主对话消费所需（其余事件集见 SDK `StreamingResponseUpdateKind.cs`，共 60+ 种）：

| 事件类 | 关键属性 | 用途 |
|---|---|---|
| `StreamingResponseOutputTextDeltaUpdate` | `Delta` (string) | 正文增量（替代 `ContentUpdate`） |
| `StreamingResponseFunctionCallArgumentsDeltaUpdate` | `ItemId`/`OutputIndex`/`Delta` (BinaryData) | 工具参数增量（替代 `ToolCallUpdates`） |
| `StreamingResponseFunctionCallArgumentsDoneUpdate` | `ItemId`/`Arguments` | 工具参数完成 |
| `StreamingResponseReasoningTextDeltaUpdate` / `DoneUpdate` | `Delta` (string) | **完整推理文本增量**（需 `include: ["reasoning"]`） |
| `StreamingResponseReasoningSummaryTextDeltaUpdate` / `DoneUpdate` | `Delta` (string) | 推理摘要增量（默认开启） |
| `StreamingResponseReasoningSummaryPartAddedUpdate` / `DoneUpdate` | `Item` | 摘要 part 生命周期 |
| `StreamingResponseOutputItemAddedUpdate` / `DoneUpdate` | `Item` (ResponseItem) | item 生命周期（可提前看类型/状态） |
| `StreamingResponseOutputTextDoneUpdate` | `ItemId`/`ContentIndex` | 文本 part 完成 |
| `StreamingResponseCompletedUpdate` | `Response` (完整 `ResponseResult`) | **usage 在此到达**（`Response.Usage`）；状态/原因 |
| `StreamingResponseFailedUpdate` / `ErrorUpdate` / `IncompleteUpdate` | `Response` / `Error` | 失败与不完整终态 |

### 4.5 推理能力

- `ResponseReasoningOptions`：`ReasoningEffortLevel`（`ResponseReasoningEffortLevel`：none/minimal/low/medium/high，wire 名 `effort`）、`ReasoningSummaryVerbosity`（auto/concise/detailed，wire 名 `summary`）、`GenerateSummary`（wire 名 `generate_summary`，**SDK 仅 internal**，需经 `Patch` 设置）。
- 完整推理文本：`options.IncludedProperties.Add("reasoning")`（SDK 只预定义 `IncludedResponseProperty.ReasoningEncryptedContent` = `"reasoning.encrypted_content"`；纯 `"reasoning"` 经隐式转换传入字符串）。
- 非流式：`ResponseResult.OutputItems` 里的 `ReasoningResponseItem`（`SummaryParts` 摘要文本 + `EncryptedContent` 加密内容）。
- `include: ["reasoning.encrypted_content"]` + `store: false` 可拿加密推理（当前不采用，无需求）。

### 4.6 usage 模型

`ResponseTokenUsage`：`InputTokenCount` / `OutputTokenCount` / `TotalTokenCount` + `InputTokenDetails.CachedTokenCount` + `OutputTokenDetails.ReasoningTokenCount`。与 `TokenUsageSnapshot`（Input/Cached/Output/Total）1:1 映射；`image_tokens`/`text_tokens` 明细在 Responses 下不在 usage 对象内，需经 `ResponseTokenUsage.Patch` 读取 `$.input_tokens_details.image_tokens`（与现状的 chat patch 读取同思路，见 §7.3）。

### 4.7 状态与截断语义

- `FunctionCallStatus`：`InProgress / Completed / Incomplete`——**`Incomplete` 即服务端确认的参数截断**（替代现状的 `IsCompleteJsonArguments` JSON 猜测 + finishReason 双重判断）。
- `ResponseStatus`：`InProgress / Completed / Cancelled / Queued / Incomplete / Failed`。
- `ResponseIncompleteStatusDetails.Reason`：`MaxOutputTokens` / `ContentFilter`（开放枚举）——**`Incomplete + MaxOutputTokens` 即 `finishReason==Length` 的等价物**。

## 5. 目标架构：传输抽象

### 5.1 接口设计（新增 `Services/Context/ICompletionTransport.cs`）

`ProcessStreamAsync` 骨架（压缩、校准、重试指令、工具执行、上下文写入、UI 回调）**整体保持不动**，只把 5 个传输敏感点抽到接口后面：

```csharp
/// <summary>一次请求的传输无关输入：由 transport 各自组装并增量回填。</summary>
public interface ICompletionTransport
{
    /// <summary>组装本轮的请求输入（chat: List&lt;ChatMessage&gt;；responses: List&lt;ResponseItem&gt; + options）。</summary>
    TransportRequest BuildRequest(EffectiveRequestRuntimeSnapshot runtime, IReadOnlyList<OpenAI.Chat.ChatMessage> chatMessages, ConversationContext context, bool includeImageBinary, bool isImageFallback);

    /// <summary>开流并归一化为内部增量序列。</summary>
    IAsyncEnumerable<NormalizedUpdate> StreamUpdatesAsync(TransportRequest request, CancellationToken cancellationToken);

    /// <summary>把「带工具调用的助手消息」追加进请求输入（chat: AssistantChatMessage+patch；responses: FunctionCallResponseItem）。</summary>
    void AppendAssistantWithTools(TransportRequest request, string content, IReadOnlyList<ToolCallInfo> toolCalls, string? reasoningContent);

    /// <summary>把工具结果追加进请求输入（chat: ToolChatMessage；responses: FunctionCallOutputResponseItem）。</summary>
    void AppendToolResult(TransportRequest request, string callId, string resultJson);

    /// <summary>请求格式版本（并入 ExecutionPolicyIdentity 的 RequestFormatVersion 语义）。</summary>
    int RequestFormatVersion { get; }
}

/// <summary>归一化流增量：主环只认这个形状。</summary>
public readonly record struct NormalizedUpdate(
    string? Text,
    string? ReasoningText,
    string? ToolCallName,        // 有值 = 工具参数增量
    string? ToolCallArgumentsDelta,
    TransportFinish? Finish,
    TokenUsageSnapshot? Usage);  // 已归一化

public readonly record struct TransportFinish(TransportFinishReason Reason, bool IncompleteToolCall);
```

实现两个：
- `ChatCompletionsTransport`：包住现有逻辑（`CompleteChatStreamingAsync` + Patch 读 `reasoning_content` + `ToolChatMessage` 回填），**行为与现状逐字节一致**（现状即此实现，回归基线）。
- `ResponsesTransport`：见 §7。

### 5.2 运行时快照扩展

- `EffectiveRequestRuntimeSnapshot`（`Services/Context/EffectiveRequestRuntimeSnapshot.cs`）持有 `ChatClient` + `ChatCompletionOptions`；新增字段：
  - `ICompletionTransport Transport`（按 §6 判定结果装配）
  - `ResponsesClient? ResponsesClient`（协议为 responses 时非空，用于 tool-loop 内重建请求/降级重试；chat 时为空）
- `OpenAiModelExecutionPolicyIdentity` 增加 `Transport` 维度（string，如 `"chat"`/`"responses"`），`RequestFormatVersion` 常量不动（传输维度已显式表达）。
- `OpenAiModelClientIdentity`（`OpenAiModelRuntimeFactory.cs:213`）增加 `Protocol` 字段——`AppConfigurationApplier` 的身份比较据此触发客户端重建（§3.4 关键前提）。

### 5.3 工具循环闭环（responses 语义）

每轮流程与现状逐点对应：

1. 首轮请求输入 = `instructions`（合并 system 提示，与现状合并 system 消息同内容）+ `input`（user/assistant/tool 历史映射为 items）。
2. 流事件归一化：`OutputTextDelta` → `Text`；`ReasoningTextDelta`（或 `ReasoningSummaryTextDelta`，见 §7.4）→ `ReasoningText`；`FunctionCallArgumentsDelta` → 工具参数增量（按 `ItemId` 建 builder，替代 chat 的 `Index`）。
3. 工具轮：`AppendAssistantWithTools` 追加 `FunctionCallResponseItem`（callId/name/args）；执行后 `AppendToolResult` 追加 `FunctionCallOutputResponseItem(callId, resultJson)`；下一轮**全量重发 input items**（`store: false`，永不 `previous_response_id`）。
4. 截断检测：`FunctionCallArgumentsDoneUpdate` 后查对应 item 的 `Status == Incomplete`，或终态 `ResponseStatus.Incomplete && Reason == MaxOutputTokens` → 走现有「丢弃 + 重试指令」分支（`OpenAIChatService.cs:837–854` 的 `rebuildTail` 逻辑原样复用）。
5. 工具执行段（`OpenAIChatService.cs:937–1027`）完全不动：审批闸门、AsyncLocal 作用域、中断修复全部传输无关。

### 5.4 推理文本双通道

- **响应侧（读取）**：chat 走 Patch 读 `reasoning_content`（现状）；responses 走 `ReasoningTextDelta`/`ReasoningSummaryTextDelta` 事件拼接。两者归一化为同一个 `assistantReasoning` StringBuilder → `ContextMessage.ReasoningContent`（`OpenAIChatService.cs:832` 起逻辑不动，UI/持久化/压缩消费点全不动）。
- **请求侧（回放）**：chat 回放 `$.reasoning_content` patch（兼容 DeepSeek 系供应商校验）；**responses 不回放推理**（官方 /responses 的 input 不需要、也不建议带 reasoning item——避免 token 浪费与未知供应商校验）。跨传输切换时，历史 `ReasoningContent` 按目标传输规则处理（responses 传输下历史推理不进请求体，只保留在上下文/压缩物中）。

## 6. 协议判定与降级

### 6.1 配置

`OpenAiProviderConfiguration` 新增：

```csharp
public enum ProviderProtocol { Auto, ChatCompletions, Responses }

[ObservableProperty] private ProviderProtocol _protocol = ProviderProtocol.Auto;
```

UI：`Views/ProviderModelsWindow.axaml`「连接」Tab（136–148 行区）插入协议下拉（ComboBox，选项来自 VM 静态集合）；本地化 key 见 §9.4。保存链路自动生效（`AppConfigurationSession.TrackAiModels` 的 PropertyChanged 统一 `RequestSave`）。

### 6.2 Auto 判定规则（保守，运行时只读，不探测）

```
resolved = provider.Protocol switch
{
    ChatCompletions => Chat,
    Responses       => Responses,
    Auto            => IsOfficialOpenAi(provider) && metadata.SupportsReasoning == true  ? Responses
                     : metadata.SupportsResponses == true                                 ? Responses
                     : Chat,   // 未知/手动 provider、非推理模型、无元数据 → Chat（最稳）
};
```

- `IsOfficialOpenAi`：`ProviderPreset == "OpenAI"` 或 BaseUrl 匹配 `api.openai.com`。
- `metadata.SupportsResponses`：来自 §9.2 的新元数据字段（OpenRouter `SupportedParameters` 含 `"responses"` 时自动置真）。
- 判定结果写入快照并进日志（`ContextPolicyResolved` 行加 `Protocol=responses`），见 §6.4。

### 6.3 失败自动降级（不硬失败）

在主环调用 `StreamUpdatesAsync` 捕获到错误时（现 `OpenAIChatService.cs:688` 位置）：

```
若 transport == Responses 且分类 ∈ { InvalidRequest(404/405), ProviderRawError } 且错误路径/文案暗示 /responses 不存在：
  → Log.Warning("Provider does not support /responses; falling back to Chat Completions. Provider={Provider}")
  → 用 ChatCompletionsTransport 重发本轮（同一 prepared 请求，无状态损失）
  → 持久记忆：本 provider 本轮会话内标记 "ResponsesUnsupported"，后续请求直接走 chat（避免每轮都失败一次）
```

- 判定复用 `_providerErrorClassifier.Classify`（`Services/ProviderErrorClassifier.cs`，`ClientResultException.Status` 已覆盖 ResponsesClient 抛出的异常类型）。
- 图片降级（`IsLikelyImageInputFailure`）、超时、限流等其他分类**不触发传输降级**，走原有错误气泡/重试逻辑。
- 明确不做：探测、预检、对 Chat Completions 方向的任何改动。

### 6.4 可见性

- 日志：`ContextPolicyResolved` 行追加 `Protocol=...`；降级时 `Log.Warning` 含 provider 名。
- 错误提示：降级发生时在输出流补一行提示（可选，跟随现有 `[API 错误: ...]` 风格），避免用户困惑"为什么突然变了"。
- 诊断页/ProviderModelsWindow 元数据区展示 `SupportsResponses` 解析值与来源（§9.2）。

### 6.5 校准/压缩的传输感知

- `ContextRequestPreparer`（`Services/Context/ContextRequestPreparer.cs`）的 `PreparedChatRequest.Features` / `ContextFingerprint` 加入 `Transport` 维度（调用方传入 runtime.Transport 标识）。
- `OpenAiModelExecutionPolicyIdentity` 加 `Transport` 后，`ExecutionPolicyIdentity` 参与的所有缓存键（压缩计划 `BaseContextFingerprint`、NotCompressible 缓存、校准训练分桶）自动隔离——**同一 provider 切换协议不会复用对方的压缩/校准历史**。
- `TokenCalibrationService.Observe` 无需改签名（Features 已含 Transport）。

## 7. 主对话流式环改造详规

### 7.1 消息 → input items 映射（ResponsesTransport）

| chat（现状 `BuildMessagesCore`） | responses |
|---|---|
| 合并 system 提示（persona+摘要信封+MCP+Skill+工作区+历史 system） | `options.Instructions`（同一字符串） |
| `UserChatMessage(文本)` | `ResponseItem.CreateUserMessageItem(文本)` |
| `UserChatMessage(文本+图片 parts)` | `CreateUserMessageItem(文本 part + CreateInputImagePart(bytes, detail))` |
| `AssistantChatMessage(正文 + tool_calls + reasoning patch)` | 正文 → `CreateAssistantMessageItem(正文)`；每个 tool_call → `CreateFunctionCallItem(id, name, args)`（平铺追加） |
| `ToolChatMessage(callId, json)` | `CreateFunctionCallOutputItem(callId, json)` |
| 历史 `SystemChatMessage`（合并后不再单独出现） | 无需映射（已并入 Instructions） |

映射器签名：`private static List<ResponseItem> BuildInputItems(ConversationContext context, string instructions, bool includeImageBinary, CancellationToken)`，放在 `ResponsesTransport` 内部；`SanitizeToolCallPairing` 的等价物：responses 由服务端校验 pairing，客户端只需保证「每个 FunctionCallResponseItem 后跟随其 FunctionCallOutputResponseItem」（构建时顺序保证即可）。

### 7.2 事件消费映射（ResponsesTransport.StreamUpdatesAsync 内部）

```
foreach (var update in client.CreateResponseStreamingAsync(options, ct))
    switch (update)
    {
        case StreamingResponseOutputTextDeltaUpdate d:       yield (Text: d.Delta);
        case StreamingResponseReasoningTextDeltaUpdate d:    yield (ReasoningText: d.Delta);
        case StreamingResponseReasoningSummaryTextDeltaUpdate d: yield (ReasoningText: d.Delta);  // 摘要并入同一通道（见 7.4）
        case StreamingResponseFunctionCallArgumentsDeltaUpdate d: yield (ToolCallName: 由 ItemId 对照, ToolCallArgumentsDelta: d.Delta.ToString());
        case StreamingResponseFunctionCallArgumentsDoneUpdate d:  // 记 args 完成 + status；查 item 列表
        case StreamingResponseOutputItemAddedUpdate a when a.Item is FunctionCallResponseItem item:
            // 建 builder（Id/Name），后续 delta 按 ItemId 命中
        case StreamingResponseOutputItemDoneUpdate a when a.Item is FunctionCallResponseItem item:
            // status == Incomplete → Finish(IncompleteToolCall: true)
        case StreamingResponseCompletedUpdate c:
            yield (Usage: MapUsage(c.Response.Usage));
            if (c.Response.Status == ResponseStatus.Incomplete
                && c.Response.IncompleteStatusDetails?.Reason == ResponseIncompleteStatusReason.MaxOutputTokens)
                yield Finish(Reason: Length);
            yield Finish(Reason: c.Response.Status switch { Failed => Error, Incomplete => Incomplete, _ => Stop });
        case StreamingResponseFailedUpdate f:
        case StreamingResponseErrorUpdate e:
            // 抛给外层统一错误分类（§6.3 降级判定）
    }
```

- 多 item 并行输出（OutputIndex/ContentIndex）在主对话单输出场景下只取第一个（与现状 `ContentUpdate` 单流语义一致）。
- `ToolCallName` 对照：`OutputItemAdded` 建 `Dictionary<itemId, builder>`；delta 按 `ItemId` 归属，避免 chat 的 index 漂移问题。

### 7.3 usage 映射

```
MapUsage(ResponseTokenUsage u) => new TokenUsageSnapshot(
    InputTokens: u.InputTokenCount, CachedInputTokens: u.InputTokenDetails?.CachedTokenCount ?? 0,
    OutputTokens: u.OutputTokenCount, TotalTokens: u.TotalTokenCount, ...);
```

- 有效性校验复用 `IsValidCalibrationUsage` 的同一套不等式（`OpenAIChatService.cs:1076–1088`，入参改为归一化后的快照）。
- `image_tokens`/`text_tokens` 明细（`ExtractInputModalityUsage` 现状走 patch）：responses 下经 `u.Patch.TryGetValue("$.input_tokens_details.image_tokens")` 读取（SDK `ResponseTokenUsage` 暴露 Patch，与 chat 同思路）；读不到则为 null，校准的 modality 偏好逻辑保持「有则用、无则缺省」。

### 7.4 推理文本通道细节（含开放决策）

- **默认取「完整推理文本」**（`include: ["reasoning"]`）并入 `ReasoningContent`——与 chat 通道的 `reasoning_content` 语义对齐，UI/压缩/持久化零改动。
- 摘要事件（`ReasoningSummaryTextDelta`）默认忽略（避免与完整文本重复拼入）。**开放问题 A**（见 §12）：若某端点只支持摘要（如第三方 /responses 的裁剪实现），降级为摘要事件作为回退通道。
- `generate_summary` SDK 仅 internal：若需关闭摘要生成（省 token），经 `ResponseReasoningOptions.Patch` 设置 `$.generate_summary = false`；默认先不设（摘要由服务端默认策略决定）。
- **请求侧不回放推理**（responses 传输下），见 §5.4；切换协议时历史推理不回发。

### 7.5 图片降级路径

- 主环内降级重发（`isImageFallback: true`）走同一 transport 接口（`BuildRequest(..., isImageFallback)` 时 chat 去图片、responses 去 image parts）——**不用图片时 items 构建一致，指纹差异由 Features 现有 ImageCount 承担**。
- `TryDescribeImagesAsync`（独立调用 #14）在 provider 协议为 responses 时跟随映射（§8 规则），其失败静默语义保持。

### 7.6 与压缩 / 校准 / 重试指令的交互

- 事务压缩：`context.RemoveMessagesById` + `messages = BuildMessages(...)` 重建在 chat 层（`OpenAIChatService.cs:637`）——responses 下等价物为 `transport.BuildRequest(runtime, rebuiltChatMessages, ...)` 重建 items。**关键约束：压缩会改写上下文，因此 responses 工具轮内也绝不能用 `previous_response_id`（服务端历史会被本地改写打断）**——与 §1 无状态决策互为印证。
- `rebuildTail`（重试指令）：chat 追加 UserChatMessage；responses 追加 `CreateUserMessageItem(指令文本)`，`BuildRequest` 时并入。
- 校准观察（`_tokenCalibration.Observe(prepared.Features, usage.InputTokenCount, ...)`）：签名不动，Features 已含 Transport（§6.5）。

## 8. 非流式调用点迁移

### 8.1 通用映射规则（每点 10–20 行）

| chat | responses |
|---|---|
| `client.CompleteChatAsync(messages, options)` | `responses.CreateResponseAsync(respOptions)` |
| `SystemChatMessage` | `options.Instructions`（单 system 场景）或 `CreateSystemMessageItem`（多 system） |
| `UserChatMessage`（含 parts） | `CreateUserMessageItem`（文本/图片 parts 同 §7.1） |
| `ChatCompletionOptions.Temperature/MaxOutputTokenCount/TopP` | `CreateResponseOptions` 同名属性（`MaxOutputTokenCount` 同名） |
| `ChatResponseFormat.CreateJsonObjectFormat()` | `TextOptions = new ResponseTextOptions { Format = ResponseTextFormat.CreateJsonObjectFormat() }` |
| `ChatTool` 列表 | `FunctionTool(name, parameters, strict)`（`FunctionDescription` 直接可搬） |
| `value.ToolCalls` / `value.Content[0].Text` | `response.OutputItems` 过滤：`FunctionCallResponseItem` → 工具；`MessageResponseItem.Content` 的 `OutputText` part → 文本 |
| `FinishReason == Length`（#12 诊断） | `response.Status == Incomplete && IncompleteStatusDetails?.Reason == MaxOutputTokens` |

响应读取辅助（放 transport 或独立静态类）：

```csharp
static string? GetFirstOutputText(ResponseResult r);           // Content[0].Text 等价
static string? GetConcatenatedOutputText(ResponseResult r);    // string.Concat(Content.Select(Text)) 等价
```

### 8.2 调用点明细（按 §3.3 编号）

| # | 迁移要点 | 特殊处理 |
|---|---|---|
| 1 子代理 | 循环结构保留（非流式逐轮），消息列表换成 items；`AppendAssistantWithTools`/`AppendToolResult` 复用 transport | 工具白名单 → `FunctionTool[]`；`EnterNonInteractive()` 不动 |
| 2/3 标题/提交信息 | 单发 + `GetFirstOutputText` | — |
| 4 KB 维护 | 同 #1 | `EnterTrusted()` 不动 |
| 5 审批 | json_object 映射；解析逻辑不动 | fail-closed 不动 |
| 6/7/8 压缩三入口 | 单发 + 文本读取（6/7 用 First，8 用 Concat） | 输入侧 ReasoningContent 渲染为文本的逻辑不动（跨传输） |
| 9/10/11 浏览器 | json_object 协商逻辑**保留**：responses 下同样尝试 `CreateJsonObjectFormat`，400/404/422 降级为不设 format 重试一次 | #10/#11 图片 part 映射 |
| 12 浏览器连接测试 | FinishReason==Length 诊断改为 Incomplete+MaxOutputTokens | **不设 Temperature 的语义保留**（Responses 下该字段为可空） |
| 13/15 连接测试 | **恒定 Chat Completions（§1 决策）** | 不迁移 |
| 14 图片识别降级 | 恒多模态 items；失败静默 | 不迁移也无妨（跟随协议映射） |

迁移顺序建议（§11）：先 #14（主环降级依赖）、#5（审批）、#6–8（压缩），后 #1/#4（循环）、#2/3、#9–12（浏览器）。全部迁移后，同一 provider 下各角色协议一致，无混用。

## 9. 配置与 UI

### 9.1 ProviderProtocol 字段（4 个文件）

1. `Models/AiModelConfiguration.cs:10–32`：`OpenAiProviderConfiguration` 加 `[ObservableProperty] private ProviderProtocol _protocol = ProviderProtocol.Auto;`（enum 定义同文件）。
2. `Views/ProviderModelsWindow.axaml`：136–148 行区（ApiRoot 与 ApiKey 之间）加协议行（TextBlock + ComboBox）。
3. `ViewModels/ProviderModelsViewModel.cs`：构造加静态选项集合（仿 `RebuildFilterOptions` 模式 476–494）；无新命令。
4. `Services/OpenAiModelRuntimeFactory.cs:213–217`：`OpenAiModelClientIdentity` 加 Protocol——**必改**，否则 `AppConfigurationApplier` 身份比较检测不到变化、不重建客户端（§3.4）。

### 9.2 SupportsResponses 元数据（8 处）

1. `Models/AiModelConfiguration.cs:55–72`：`ModelMetadataOverrides` 加 `_supportsResponses` + `HasAnyValue`。
2. `Models/ModelMetadataModels.cs:100–112`：`ResolvedModelMetadata` 加 `SupportsResponses`。
3. `Services/ModelMetadata/ModelMetadataResolver.cs:59–70`：`ResolveCapability(profile?.Overrides.SupportsResponses, matched, openRouterSource, "responses")`。
4. `ViewModels/ProviderModelMetadataItemViewModel.cs`：`ResponsesText` + `SupportsResponsesOverride` + `ReloadFromProfile` + `NotifyResolvedProperties`（仿 79/171–181/240/293 行）。
5. `Views/ProviderModelsWindow.axaml`：313 行区展示行 + 360 行区三态 CheckBox。
6. `Services/OpenAiModelRuntimeFactory.cs:127`：`ComputeProfileRevision` 纳入新字段（执行策略刷新依据）。
7. 可选：CSV 导出（`ProviderModelsViewModel.ToCsvRow` + `ModelMetadataCsvExporter`）。
8. 消费方：§6.2 Auto 判定（`OpenAIChatService` / `OpenAiModelRuntimeFactory`）。

### 9.3 持久化

- 无需 schema 迁移：旧 config.json 缺字段由属性初始化器兜底（`ConfigService.DeserializeConfig` 152–202 行的版本分支不动）。
- 可选加固：`Services/AppConfigNormalizer.cs` 加 `NormalizeProtocol`（非法枚举钳制回 Auto），挂在 `AppConfigurationSession.Normalize` 与 `ConfigService.SaveAsync`。
- 可选可读性：`ConfigService.cs:17–21` `JsonOptions` 加 `JsonStringEnumConverter`（协议枚举存字符串；对旧文件反序列化无影响）。若加，需确认 ProviderPreset 等既有枚举字段不受影响（无既有枚举字段序列化依赖数字的用例）。

### 9.4 本地化 key（两个 Locale 文件各加）

```
ProviderModels.Protocol                 "协议" / "Protocol"
ProviderModels.Protocol.Auto            "自动" / "Auto"
ProviderModels.Protocol.ChatCompletions "Chat Completions"（双语同名）
ProviderModels.Protocol.Responses       "Responses"（双语同名）
ProviderModels.Metadata.Responses       "Responses：" / "Responses:"（仿 ProviderModels.Metadata.Reasoning）
```

## 10. 测试计划

### 10.1 复用现有基础设施

- 假管道层在 HTTP（3 个 SSE handler 输出 `chat.completion.chunk`），5 处 reflection 注入 `_chatClient`（2796/2981/3075/3151/3216）。
- Responses transport 落地后：**新增 responses 格式 SSE 夹具**（`response.created` → `response.output_item.added` → `response.output_text.delta` / `function_call_arguments.delta` → `response.completed`（含 usage））；5 处注入点迁移到新字段（`_responsesClient` 或 transport 字段），旧夹具保留回归 ChatCompletionsTransport。
- 规则：新用例用 `PumpUntil`；新逻辑先在隔离环境（构造 VM/服务 + 夹具）验证再并入套件；经 `Scripts/run-headless-tests.ps1/.sh` 运行。

### 10.2 新增用例矩阵

| 用例 | 断言 | 备注 |
|---|---|---|
| Responses 流式正文 + usage | 每轮各报一次 usage，顺序 `usage → tool → usage`（镜像 `TestImmediateToolCallUsageAsync` 2993–3004） | 新夹具 |
| Responses 推理文本提取 | `StreamMessageAsync` 产出 `ChatMessage.ReasoningContent` 正确拼接（完整文本事件）；`ContextRequestPreparer.GetReasoningContent` 可读 | **现状空白** |
| Responses 工具循环 | 两轮 function_call 事件 → `ImmediateUsageFunctionRegistry` 执行 → 第二轮含 `FunctionCallOutputResponseItem` 语义 | 镜像 `ToolLoopSseHandler` |
| Responses 截断重试 | `FunctionCallArgumentsDoneUpdate` + `Status=Incomplete` → 重试指令分支；同 revision 指纹缓存（镜像 3112–3171） | |
| Responses 压缩联动 | 大 tool 结果触发 ID 级压缩，压缩事件时序（镜像 3173–3250） | |
| usage 形状映射 | `input_tokens`/`cached_tokens`/`output_tokens` 归一化后 `TryApplyUsage` 通过；`image_tokens` patch 读取 | |
| 端点降级 | responses 夹具返回 404 → 自动降级 chat 重发成功；同 provider 后续请求直接走 chat | 新语义 |
| 图片降级 | 夹具先图片拒绝错误 → 文本路径重发成功（`isImageFallback` 指纹） | **现状空白**，chat 与 responses 各一 |
| 协议 Auto 判定 | 官方+推理→responses；目录含 responses→responses；未知→chat（纯单元，无网络） | |
| 连接测试恒定 chat | TestConnectionAsync 始终走 chat 夹具 | |

### 10.3 Archive.Tests

传输无关，无需改动；可在 Archive 侧补一条闭环：transport 产出的 ReasoningContent → 压缩物（现已有 1961–2059 覆盖，无新增必要）。

## 11. 分阶段实施计划

| Phase | 内容 | 验收标准 | 涉及文件（主要） |
|---|---|---|---|
| **P0** 协议配置管道 | ProviderProtocol 字段 + UI + 本地化；`OpenAiModelClientIdentity` 纳入协议；Auto 判定服务（纯函数，不接传输） | 设置可保存/恢复协议；日志打印判定结果；切换协议触发 `UpdateConfig`（identity 变化） | `AiModelConfiguration.cs`、`ProviderModelsWindow.axaml`、`ProviderModelsViewModel.cs`、`OpenAiModelRuntimeFactory.cs`、`Locale.*.axaml` | ✅ 完成 |
| **P1** 传输抽象重构 | `ICompletionTransport` + `ChatCompletionsTransport`（行为与现状逐字节一致）+ 快照扩展 + ExecutionPolicyIdentity 加 Transport | 现有 5 个流式测试全绿（回归基线）；校准指纹含 Transport | 新建 `Services/Context/ICompletionTransport.cs` + 实现；`OpenAIChatService.cs`、`EffectiveRequestRuntimeSnapshot.cs` | ✅ 完成 |
| **P2** Responses transport（主环） | `ResponsesTransport`：BuildRequest/StreamUpdates/回填/usage/截断/推理文本 + 端点降级 | 新夹具下：流式/工具循环/截断重试/usage/推理文本用例全绿；`include:["reasoning"]` 生效 | `Services/Context/ResponsesTransport.cs`、`OpenAIChatService.cs`（装配点）、HeadlessTests 新夹具与注入点 | ✅ 完成（5 个新用例） |
| **P3** 非流式调用点铺开 | 按 §8.2 顺序迁移；`GetFirstOutputText`/`GetConcatenatedOutputText` 辅助 | 同 provider 各角色协议一致；审批/压缩/浏览器协商行为不变 | 15 个调用点 + 辅助静态类 | ✅ 完成（两处连接测试按 §1 决策保持 chat） |
| **P4** 元数据 + 测试收尾 | `SupportsResponses` 8 处；Auto 判定接元数据；端到端用例补全；文档更新 | 全部新用例绿；Archive.Tests 不受影响 | 见 §9.2 清单 | ✅ 完成（图片降级 chat+responses 端到端、端点降级、Auto 判定、reasoning 提取共 8 个新用例全绿；CSV 导出列为可选项未做） |

里程碑说明：
- P0 独立可交付（纯配置，无传输风险）。
- P1 是关键回归点：重构期间主对话必须保持现有行为，靠现有 5 个流式测试 + 手动冒烟把关。
- P2 是唯一新算法集中的阶段，建议先在隔离环境用 OpenRouter/百炼的 `/responses` 端点实测事件流，再写正式用例。
- P3 是机械劳动，风险最低但量最大。

## 12. 风险与开放问题

| # | 问题 | 影响 | 对策/建议 |
|---|---|---|---|
| A | 第三方 /responses 端点的推理实现差异（只给摘要不给完整文本；`include` 被忽略） | 推理文本可能为空或只到摘要 | 摘要事件作为回退通道（§7.4）；以各家文档为准，文档记录已验证端点 |
| B | `generate_summary` SDK 仅 internal，需 Patch 设置 | 关闭摘要生成需额外 Patch 代码 | 默认不关；如需省 token 再补 Patch 路径并加测试 |
| C | 第三方 /responses 事件顺序/usage 字段偏差 | 归一化错乱、压缩判断失真 | 归一化层宽容处理（缺失字段走缺省）；文档记录已验证端点 |
| D | `Experimental("OPENAI001")` API 面演进 | SDK 升级时类型/属性变动 | 升级前查 CHANGELOG（`openai-dotnet/CHANGELOG.md` 488–491 等条目）；Responses 相关集中在 transport 内部，隔离面小 |
| E | 切换协议后压缩/校准历史不共享（指纹隔离） | 切换初期压缩阈值判断用新桶的冷数据 | 符合预期；日志可见（§6.4），文档说明 |
| F | 同 provider 混用 chat 兼容端点与 /responses 的行为差异（如 usage 口径） | 用户困惑 | 判定规则保守（§6.2）+ 降级提示 + 日志 |
| G | `ContextRequestPreparer.GetReasoningContent` 读的是 `AssistantChatMessage.Patch`（chat 形状） | 若估算侧未来要直接吃 responses 产物，需适配 | 现状通过内部 `ContextMessage.ReasoningContent` 中转，无需改；保留注释 |

## 13. 决策变更记录

| 日期 | 变更 | 原因 | 迁移影响 |
|---|---|---|---|
| 2026-08-07 | 初版定稿 | — | — |
| 2026-08-07 | P0–P3 实施完成；P4 进行中 | 见上表勾选 | 无 |
| 2026-08-07 | P4 完成：图片降级端到端（chat+responses）、Auto 判定、端点降级、reasoning 提取用例全部落地 | 8 个新用例全绿 | 无 |
| 2026-08-07 | `BuildContentParts` 对无 MediaType 的图片字节补 `image/png` 兜底 | SDK 的 `CreateInputImagePart(BinaryData)` 强制要求 MediaType 非空，chat 侧字节不带 | 无 |
| 2026-08-07 | 非主对话角色的 Auto 判定按「显式协议 + 保守 chat」实现（`ResponsesCallHelpers.ShouldUseResponses` 只认显式 Responses） | 这些角色没有元数据解析管线，接 Auto 需逐角色引入 `ModelMetadataResolver`，收益低 | 主对话 Auto 完整生效；其余角色 Auto 等同 chat（显式切换仍生效），如需升级按 §8.1 补元数据 |
| 2026-08-07 | 浏览器角色走 `EffectiveBrowserAgentConfig.ToEffectiveOpenAiModel()` 转换进辅助类 | 浏览器有独立解析配置类型，辅助类统一吃 `EffectiveOpenAiModel` | 无 |
| 2026-08-07 | `OPENAI001` 在本项目为编译错误（非警告），涉及 Responses 类型的文件统一文件级 `#pragma warning disable OPENAI001` | 该诊断被配置为 error；响应面类型全部为 Experimental | 新文件需遵循同一约定 |
| 2026-08-07 | `Athena.Archive.Tests` 的 office preview 会话测试在 Windows 上因 `/tmp` 路径归一化失败（`D:\tmp\report.pdf`） | 环境路径行为，与本次改动无关 | 已确认非本特性引入；CI/Linux 无此问题 |
