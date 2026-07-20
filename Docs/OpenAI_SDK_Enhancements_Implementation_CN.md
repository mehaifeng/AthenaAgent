# OpenAI SDK 增强实施方案：TTS 与自动重试

> 文档状态：已实施（2026-07-20）
> 编写日期：2026-07-20
> 目标版本：Athena.UI 当前依赖的 `OpenAI 2.12.0` / `System.ClientModel 1.14.0`
> 范围：远程 TTS 改用 `AudioClient`、统一 SDK 重试策略
> 落地结果：远程 TTS 已迁移至 `AudioClient.GenerateSpeechAsync`；所有 OpenAI SDK 客户端已使用统一的重试与超时配置；Debug、Release 构建及项目测试均通过。

---

## 1. 结论与关键决策

本次改造采用两个彼此独立、可分步合并的变更：

1. **远程 TTS 改用 `OpenAI.Audio.AudioClient`**。保留现有 `IChatService`、音频附件、自动播放和 System TTS 路径，只替换 `OpenAIChatService` 内手写的 `POST /audio/speech`。
2. **统一并显式配置 SDK 自动重试**。`OpenAI 2.12.0` 默认已经对 408、429、500、502、503、504 使用指数退避，并最多额外重试 3 次。因此这里不是“再加一层重试循环”，而是建立统一的 `OpenAIClientOptions` 工厂，显式固定重试和超时策略，消除各服务各自创建客户端造成的策略漂移。

### 1.1 推荐默认值

| 配置 | 默认值 | 说明 |
|---|---:|---|
| SDK 最大重试次数 | 3 | 与 OpenAI SDK 2.12.0 默认行为一致，含义是首次请求失败后最多再试 3 次 |
| SDK 单次网络操作超时 | 读取现有 `AppConfig.Timeout` | 保留当前用户对网络超时的控制 |
| TTS 输出格式 | MP3 | 与现有附件 MIME、扩展名和播放器链路一致 |

### 1.2 明确不做的事情

- 不迁移到 Responses API。
- 不增加语音输入或音频转录。
- 不增加 Embedding 维度配置，不改动现有向量生成与索引重建逻辑。
- 不改动 System TTS。
- 不给工具执行增加重试；SDK HTTP 重试与 Athena 工具调用循环必须保持隔离。
- 不让 SDK 重试策略覆盖 MinerU、Tavily、GitHub 更新、MCP、Playwright 等非 OpenAI SDK 网络请求。
- 不在第一版开放“重试次数”高级设置；先使用统一且可测试的固定值 3，避免配置面膨胀。

---

## 2. 当前实现基线

### 2.1 TTS

当前远程 TTS 位于 `Services/OpenAIChatService.cs`：

- `GenerateSpeechAttachmentAsync` 解析独立音频配置。
- `System` provider 转到 `GenerateSystemSpeechAttachmentAsync`。
- 其他 provider 在方法内创建 `HttpClient`，手工构造 Bearer Token 和 JSON，然后调用 `ChatAudioBaseUrl`。
- 响应按 `audio/mpeg` 读取并交给 `AttachmentStoreService`，最终仍由现有音频播放链路处理。

当前设计的优点是 TTS 与主对话凭据隔离；本次迁移必须保留这一点。

### 2.2 OpenAI SDK 客户端

项目在多处直接构造 `OpenAIClientOptions` / `OpenAIClient`：

- `Services/OpenAIChatService.cs`
- `Services/OpenAIEmbeddingService.cs`
- `Services/OpenAIImageGenerationService.cs`
- `Services/OpenAiModelRuntimeFactory.cs`
- `Services/Browser/BrowserTaskPlanner.cs`
- `Services/Browser/BrowserVisionService.cs`
- `Services/SubAgents/SubAgentRunner.cs`
- `Services/KnowledgeBaseMaintenanceRunner.cs`
- `Services/ModelCatalogService.cs` 的 OpenAI 模型列表路径

这些客户端已经继承 SDK 默认重试，但代码没有统一声明策略，日志也无法明确区分“首次请求失败”和“最终重试耗尽”。

## 3. 方案一：远程 TTS 改用 AudioClient

### 3.1 调用链保持不变

改造后的调用链：

```text
ChatTabViewModel
  -> IChatService.GenerateAssistantSpeechAsync
  -> OpenAIChatService.GenerateSpeechAttachmentAsync
      -> System provider: ISystemAudioService（保持不变）
      -> Remote provider: OpenAIClient.GetAudioClient(model)
          -> AudioClient.GenerateSpeechAsync
      -> AttachmentStoreService.CreateGeneratedAudioAsync（保持不变）
      -> 现有 LibVLC / 系统播放器（保持不变）
```

不新增公开服务接口，避免为了替换一个传输实现而改动聊天 ViewModel 和音频 UI。将来增加 STT/Realtime 时，再把音频能力抽成独立服务。

### 3.2 SDK 调用形式

目标代码形态如下（示意，以实际编译 API 为准）：

```csharp
var options = OpenAiClientOptionsFactory.Create(
    baseUrl: AudioConfigResolver.GetSdkBaseUrl(audioConfig.BaseUrl),
    timeoutSeconds: _config.Timeout);

var openAiClient = new OpenAIClient(
    new ApiKeyCredential(audioConfig.ApiKey),
    options);

var audioClient = openAiClient.GetAudioClient(audioConfig.Model);

var speechOptions = new SpeechGenerationOptions
{
    ResponseFormat = GeneratedSpeechFormat.Mp3
};

GeneratedSpeechVoice voice = audioConfig.Voice;
ClientResult<BinaryData> response = await audioClient.GenerateSpeechAsync(
    text,
    voice,
    speechOptions,
    cancellationToken);

byte[] audioBytes = response.Value.ToArray();
```

`GeneratedSpeechVoice` 是可由字符串构造/转换的可扩展值类型，因此现有配置中的声音名称不需要改成硬编码枚举，也不会限制兼容 provider 的自定义声音。

### 3.3 Base URL 兼容处理

这里是本次 TTS 迁移最容易出错的点。

当前 `ChatAudioBaseUrl` 保存的是**完整 speech endpoint**：

```text
OpenAI:     https://api.openai.com/v1/audio/speech
OpenRouter: https://openrouter.ai/api/v1/audio/speech
```

而 `OpenAIClientOptions.Endpoint` 需要的是 **API 根地址**，SDK 会自行追加 `/audio/speech`：

```text
OpenAI:     https://api.openai.com/v1
OpenRouter: https://openrouter.ai/api/v1
```

为了避免配置 schema 迁移和破坏已有用户配置，第一版不改变 `ChatAudioBaseUrl` 的持久化语义，而是在 `AudioConfigResolver` 新增纯函数：

```csharp
public static string GetSdkBaseUrl(string configuredUrl)
```

规则：

1. 去掉尾部 `/`。
2. 若路径以 `/audio/speech` 结尾，则只移除该后缀。
3. 否则将配置值视为 API 根地址原样使用。
4. 只处理路径后缀，不做字符串全局替换，避免误伤 host、query 或中间路径。
5. URL 非法时在创建客户端前返回清晰的本地化错误。

这样既兼容现有完整 endpoint，也允许高级用户直接填写 API 根地址。

### 3.4 行为保持与错误处理

必须保持：

- 音频凭据继续独立于主模型凭据，禁止自动继承主 API Key。
- provider 为 `System` 时不创建 SDK 客户端。
- 成功结果仍保存为 `.mp3`、`audio/mpeg`。
- `AudioProvider` 元数据、自动播放、手动播放和停止行为不变。
- `TestAudioOutputAsync` 与正式生成共用同一条 SDK 路径。
- 用户取消必须传播 `CancellationToken`，不能转换成普通失败。

错误处理改为捕获 `ClientResultException`，日志至少记录：provider、model、HTTP status、request id（存在时）和最终错误；绝不能记录 API Key 或完整请求文本。

SDK 2.12.0 文档注明 TTS 文本最大长度为 4096 字符。第一版保持 Athena 现有行为，在本地截取前 4096 字符，不在本次实现中拼接多个 MP3。长文本分段 TTS 可作为后续独立功能。

### 3.5 删除的旧实现

完成迁移后，从 `OpenAIChatService` 删除仅供旧 TTS 使用的代码：

- TTS 专用 `HttpClient` 创建；
- 手写 Authorization / Accept header；
- 手写匿名 JSON 请求体；
- `PostAsync` 和 `ReadAsByteArrayAsync` 路径；
- 已无其他用途的 `System.Net.Http.Headers` 等 using。

不要删除图像下载、Web Search 等仍在使用的 `HttpClient`。

---

## 4. 方案二：统一 SDK 自动重试

### 4.1 事实基线

`OpenAI 2.12.0` 默认已经对以下状态最多额外重试 3 次，并使用指数退避：

- 408 Request Timeout
- 429 Too Many Requests
- 500 Internal Server Error
- 502 Bad Gateway
- 503 Service Unavailable
- 504 Gateway Timeout

所以当前所有通过 SDK 创建的 Chat、Embedding、Image 和 Model 客户端实际上已经有自动重试。手写 TTS 不在 SDK 管线中，迁移到 `AudioClient` 后才会自动获得同样能力。

### 4.2 新增统一 Options 工厂

已新增：

```text
Services/OpenAiClientOptionsFactory.cs
```

职责仅限创建一致的 `OpenAIClientOptions`：

```csharp
public static class OpenAiClientOptionsFactory
{
    public const int DefaultMaxRetries = 3;

    public static OpenAIClientOptions Create(
        string? baseUrl,
        int timeoutSeconds)
    {
        var options = new OpenAIClientOptions
        {
            RetryPolicy = new ClientRetryPolicy(DefaultMaxRetries),
            NetworkTimeout = TimeSpan.FromSeconds(NormalizeTimeoutSeconds(timeoutSeconds))
        };

        if (!string.IsNullOrWhiteSpace(baseUrl))
            options.Endpoint = new Uri(baseUrl.Trim());

        return options;
    }
}
```

需要引用 `System.ClientModel.Primitives`。工厂应验证 timeout，建议限制在 10～600 秒；非法值回退到当前默认 60 秒。

显式设置 `ClientRetryPolicy(3)` 的目的不是改变 SDK 行为，而是：

- 让策略成为 Athena 可审查的代码契约；
- 保证所有 SDK client 使用同一超时和重试上限；
- 后续升级 SDK 时不会无意接受默认值变化；
- 便于通过自定义 transport 做确定性测试。

### 4.3 接入范围

所有业务代码中的 `new OpenAIClientOptions()` 已替换为统一工厂，覆盖：

1. 主聊天；
2. `OpenAiModelRuntimeFactory` 创建的标题、压缩、审批等角色；
3. Embedding；
4. 图像生成；
5. 浏览器规划与视觉模型；
6. 子代理；
7. 知识库维护；
8. 新的 AudioClient；
9. SDK 模型目录请求。

`OpenAiModelRuntimeFactory.CreateClient` 作为多数非主聊天角色的收口点已优先接入；其他直接创建 client 的服务也已使用同一工厂。

### 4.4 不能增加应用级重试循环

禁止在以下层级再包 `for` / Polly 重试：

- `OpenAIChatService` 的工具调用循环；
- `SubAgentRunner`；
- `KnowledgeBaseMaintenanceRunner`；
- `FunctionRegistry.ExecuteAsync`；
- 任意写文件、终端、浏览器操作或 MCP 工具执行。

原因是应用级重试看不到服务端是否已处理请求，可能产生重复模型输出、重复扣费或重复副作用。重试只由 SDK pipeline 对单个 HTTP 调用负责。

流式聊天还需遵守：一旦已经向 UI 产出任何文本 delta，断流后不得自动从头重放，否则会造成重复文本。SDK 管线重试不能被描述为“流式断点续传”；UI 保持现有失败/用户重试语义。

### 4.5 不在本次范围内的网络调用

以下调用不会因为统一 SDK options 而获得重试：

- OpenRouter 特殊模型目录的手写 HTTP；
- 图像 URI 的二次下载；
- Web Search；
- MinerU；
- GitHub 更新；
- MCP transport；
- 浏览器页面请求。

文档和日志必须明确这个边界，避免用户误以为“所有网络请求都已自动重试”。这些路径如需重试，应单独评估幂等性后实施。

---

## 5. 文件变更清单

| 文件 | 实际变更 |
|---|---|
| `Services/OpenAiClientOptionsFactory.cs` | 新增统一 Endpoint、NetworkTimeout、ClientRetryPolicy 构造逻辑 |
| `Services/OpenAIChatService.cs` | 远程 TTS 从手写 HTTP 改为 AudioClient；接入统一 options；保留 System TTS 与附件链路 |
| `Services/AudioConfigResolver.cs` | 新增 speech endpoint 到 SDK API root 的安全转换 |
| `Services/OpenAiModelRuntimeFactory.cs` | 使用统一 options 工厂 |
| `Services/OpenAIEmbeddingService.cs` | 仅使用统一 options 工厂，不改变 Embedding 请求参数 |
| `Services/OpenAIImageGenerationService.cs` | 使用统一 options 工厂 |
| `Services/Browser/BrowserTaskPlanner.cs` | 使用统一 options 工厂 |
| `Services/Browser/BrowserVisionService.cs` | 使用统一 options 工厂 |
| `Services/SubAgents/SubAgentRunner.cs` | 使用统一 options 工厂 |
| `Services/KnowledgeBaseMaintenanceRunner.cs` | 使用统一 options 工厂 |
| `Services/ModelCatalogService.cs` | SDK 路径使用统一 options；保留 OpenRouter 特殊 HTTP 路径 |
| `Athena.Archive.Tests/Program.cs` | 增加 options、TTS URL 与音频 SDK 路径测试 |

如果实施时发现多个直接创建 client 的服务需要传入不同 timeout，不新增多套工厂；应由同一个工厂接受显式 timeout 参数。

---

## 6. 分阶段实施记录

### 阶段 A：统一 SDK 管线（已完成）

1. 新增 `OpenAiClientOptionsFactory`。
2. 显式设置 `ClientRetryPolicy(3)` 与 `NetworkTimeout`。
3. 先接入 `OpenAiModelRuntimeFactory`、主聊天和 Embedding。
4. 再替换其余直接构造 `OpenAIClientOptions` 的位置。
5. 运行现有测试，确认调用行为未变化。

这一阶段理论上是行为保持改造，因为重试次数与 SDK 当前默认值一致。

### 阶段 B：TTS 切换 AudioClient（已完成）

1. 增加 `GetSdkBaseUrl` 和 URL 单元测试。
2. 用 `AudioClient.GenerateSpeechAsync` 替换手写 HTTP。
3. 继续输出 MP3 并复用附件存储。
4. 验证 OpenAI、OpenRouter、自定义标准兼容端点和 System provider。
5. 删除 TTS 已不再需要的 HTTP 代码。

### 阶段 C：回归与文档更新（已完成）

1. 更新 `README.md` / `README_CN.md` 的 SDK 能力描述。
2. 更新 `AGENTS.md` 中 TTS 实现描述（从手写 HTTP 改为 AudioClient）。
3. 执行 Debug/Release build 和测试项目。
4. 在本文件顶部补充最终落地状态，并在测试记录中登记结果。

---

## 7. 测试计划

### 7.1 单元测试

#### AudioConfigResolver

- OpenAI 完整 speech URL 正确转换为 `/v1`。
- OpenRouter 完整 speech URL 正确转换为 `/api/v1`。
- 已是 API root 时保持不变。
- 大小写、尾部 `/`、query、非法 URI 行为明确。
- `System` provider 不进入远程 URL 转换调用链。

#### OpenAiClientOptionsFactory

- Endpoint 正确。
- `NetworkTimeout` 使用配置值。
- 过小、过大、负数 timeout 被归一化。
- `RetryPolicy` 为 `ClientRetryPolicy`，最大重试数由常量固定为 3。
- 通过可计数的 fake transport 验证：429 两次后成功时总请求 3 次；连续失败时不超过 4 次总请求。
- `CancellationToken` 取消后停止等待和重试。

### 7.2 集成测试

- 使用有效 OpenAI 配置生成测试语音，附件非空、可播放、MIME 为 `audio/mpeg`。
- 音频连接测试与真实聊天 TTS 使用同一实现。
- 401/404/429/5xx 最终错误能显示可理解信息，不泄露密钥。

### 7.3 必跑命令

```powershell
dotnet build Athena.UI.sln
dotnet build Athena.UI.sln -c Release
dotnet run --project Athena.Archive.Tests/Athena.Archive.Tests.csproj
```

### 7.4 实施验证记录（2026-07-20）

- Debug 构建通过，0 警告、0 错误。
- Release 解决方案构建通过，0 警告、0 错误。
- Release 配置运行项目测试，64 项全部通过，其中包含新增的 TTS Endpoint 归一化和统一重试/超时策略测试。
- 未使用真实供应商凭据执行在线 TTS 集成测试；OpenAI、OpenRouter 与自定义兼容服务的实际网络响应仍建议在发布前通过应用内“测试语音”各验证一次。

---

## 8. 验收标准

同时满足以下条件才算完成：

- `OpenAIChatService` 不再手写远程 TTS HTTP 请求。
- OpenAI/OpenRouter/Custom 标准兼容端点生成语音时没有 `/audio/speech/audio/speech` 路径重复。
- System TTS、音频附件、测试播放、自动播放没有行为回归。
- 所有 OpenAI SDK client 使用统一 options 工厂，重试策略明确为最多 3 次额外尝试。
- 没有在模型工具循环或业务层新增第二套自动重试。
- Debug/Release 构建和全部现有/新增测试通过。

---

## 9. 风险与回滚

| 风险 | 预防措施 | 回滚方式 |
|---|---|---|
| TTS Base URL 被 SDK 再追加路径 | 集中 `GetSdkBaseUrl`，覆盖完整 endpoint/root 两种配置测试 | 恢复旧 TTS 方法；配置文件无需回滚 |
| 第三方 provider 与 SDK 请求格式不完全兼容 | 连接测试先行，错误中显示 provider/status；不静默降级 | 针对该 provider 临时恢复手写实现或标记不兼容 |
| 重试增加最坏响应时间 | 统一 NetworkTimeout，尊重取消令牌，日志记录最终耗时 | 将 `DefaultMaxRetries` 调低；不改业务层 |
| 流式响应中途失败产生重复内容 | 已输出 delta 后不做应用级重放 | 保持现有失败提示与用户手动重试 |

TTS 配置不改持久化格式，因此回滚代码后仍可继续使用原完整 endpoint。

---

## 10. 官方依据

- OpenAI .NET SDK 客户端与 `OpenAIClient.GetAudioClient`：<https://github.com/openai/openai-dotnet#using-the-openaiclient-class>
- OpenAI .NET SDK 默认自动重试行为：<https://github.com/openai/openai-dotnet#automatically-retrying-errors>
- SDK 公共 API 定义：<https://github.com/openai/openai-dotnet/blob/main/api/OpenAI.netstandard2.0.cs>

实施时以项目锁定的 `OpenAI 2.12.0` 公共 API 和本地 XML 文档为编译基线；官方仓库 `main` 仅用于补充最新说明，不能用尚未进入 2.12.0 的 API 直接编写代码。
