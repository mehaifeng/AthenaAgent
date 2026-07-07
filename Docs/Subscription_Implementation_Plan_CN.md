# Athena 订阅服务落地计划

> 状态：**草案（待评审）** ｜ 目标分支：`feature/subscription` ｜ 撰写日期：2026-07-07
> 关联文档：`Docs/ToolApproval_Implementation_CN.md`（门控机制先例）、`Docs/PrivacyPolicy.md`（需同步修订）

---

## 1. 目标与非目标

### 1.1 目标

1. 新增**订阅托管模式**（Subscription）：用户登录 Athena 账户后，模型请求经我方网关转发，无需自备 API Key；档位（Tier）决定可用模型质量、可用功能与月度配额。
2. 与现有**自备 Key 模式**（BYOK，即当前的自定义 Provider/BaseUrl/ApiKey 形态）**完全隔离共存**：
   - 代码层：在独立分支 `feature/subscription` 上开发，主干随时可发版；
   - 运行时层：两种模式互斥切换、配置互不污染、可随时无损往返；
   - 数据层：账户与订阅状态不写入 `config.json`，存放独立文件。
3. 订阅模式下客户端只认**逻辑模型名**（如 `athena-primary`），真实型号由网关映射，换供应商不需要发版。
4. 保持 BYOK 作为永久免费路径，现有用户零迁移成本、零行为变化。

### 1.2 非目标（本期不做）

- 应用内嵌支付页面（一律跳系统浏览器到托管收银台）；
- 团队/多席位订阅；
- 客户端侧的用量硬闸（配额裁决权只在服务端，客户端只做展示与预警）；
- BYOK 模式的任何行为变更。

---

## 2. 隔离原则（本计划的最高约束）

| 层面 | 约束 | 落点 |
|---|---|---|
| 分支 | 所有订阅代码在 `feature/subscription`；每个 Phase 结束点保证可编译、BYOK 回归通过，主干合并不阻塞其他特性 | Phase 0 |
| 功能开关 | `SubscriptionFeatureEnabled` 编译期/隐藏配置开关，默认 **false**。关闭时：不注册账户服务 UI 入口、不显示模式切换、行为与主干完全一致。这保证分支可以随时安全合回 main 而不提前曝光 | Phase 0 |
| 运行时 | `ModelAccessMode` 枚举（`Custom` / `Subscription`）是唯一的模式判定点，所有分叉逻辑收敛到统一凭据解析入口，禁止在业务代码里散落 `if (mode == ...)` | Phase 1/3 |
| 配置 | 订阅模式**不读不写** BYOK 的任何凭据字段；BYOK 模式不感知账户状态。切换模式 = 只改 `ModelAccessMode` 一个值 | Phase 3 |
| 数据 | 账户令牌、档位缓存放 `AthenaData/account.json`（独立于 `config.json`），理由见 §4.2 | Phase 2 |
| AI 边界 | AI 自配置工具（`view_self_configuration` / `modify_self_configuration`）与文件工具**不可读写**任何账户数据，见 §7.G | Phase 2/4 |

---

## 3. 总体架构

### 3.1 组件图

```
┌───────────────────────────── Athena.UI (本仓库) ─────────────────────────────┐
│                                                                              │
│  ChatTab / SubAgents / Browser / KB维护 / 历史摘要 / 生图 / TTS / Embedding    │
│        │                                                                     │
│        ▼                                                                     │
│  IModelEndpointResolver (Phase 1 新增，统一凭据出口)                           │
│        ├── Custom 模式 → 现有 ModelCredentialResolver 继承树（行为不变）        │
│        └── Subscription 模式 → AccountService 提供 (网关URL, 账户令牌, 逻辑模型名)│
│                                                                              │
│  AccountService (Phase 2 新增)                                               │
│    登录/登出/单飞刷新令牌/授权缓存(entitlement)/用量查询                        │
│    持久化: AthenaData/account.json                                           │
│                                                                              │
│  EntitlementGate (Phase 4 新增)                                              │
│    FunctionRegistry.FilterTools 扩展 / 子代理并行数 clamp / 上下文上限 clamp   │
└──────────────────────────────────┬───────────────────────────────────────────┘
                                   │ HTTPS (Bearer 账户令牌)
                                   ▼
┌────────────────────────── athena-server (独立新仓库) ────────────────────────┐
│  BFF/账户服务: 邮箱验证码登录、JWT 签发、订阅状态、用量查询、支付 webhook       │
│  LLM 网关: OpenAI 兼容代理（选型 new-api 或 LiteLLM Proxy，见 §10）           │
│    校验令牌 → 查档位/配额 → 逻辑模型名→真实模型映射 → 转发 → 记账              │
│  真实供应商 Key 只存在这一层                                                  │
└──────────────────────────────────┬───────────────────────────────────────────┘
                                   ▼
                     OpenAI / Anthropic / DeepSeek / Qwen ...
```

### 3.2 逻辑模型名协议

客户端在订阅模式下**永不出现真实型号字符串**。逻辑名与角色一一对应：

| 逻辑模型名 | 对应角色 | 现有配置字段（BYOK 模式仍用） |
|---|---|---|
| `athena-primary` | 主模型 | `Model` |
| `athena-secondary` | 次级（摘要/压缩/历史标题） | `SecondaryModel` |
| `athena-embedding` | 嵌入 | `EmbeddingModel` |
| `athena-image` | 生图 | `ImageGenerationModel` |
| `athena-tts` | 语音合成 | `ChatAudioModel` |
| `athena-vision` | 浏览器智能体视觉 | `BrowserAgentModel` |
| `athena-subagent` | 子代理 | `SubAgentModel` |
| `athena-maintenance` | KB 整理 | `KnowledgeMaintenanceModel` |

网关侧维护 `档位 × 逻辑名 → 真实模型` 映射表，可按地区路由不同后端（合规考虑，见 §10）。
**嵌入模型例外约束**：`VectorStoreService` 持有嵌入模型指纹，网关侧 `athena-embedding` 的底层模型**一经上线不可随意更换**（更换 = 全量向量失效需重建索引）。如必须更换，需要走带版本号的新逻辑名（`athena-embedding-v2`）+ 客户端重建索引流程，单独立项。

### 3.3 服务端 API 契约（客户端开发以此为准，先行 mock）

```
POST /auth/otp/request        { email }                → 202
POST /auth/otp/verify         { email, code, device }  → { accessToken, refreshToken, expiresIn }
POST /auth/token/refresh      { refreshToken }         → { accessToken, refreshToken, expiresIn }
POST /auth/logout             { refreshToken }         → 204

GET  /account/me              → { email, plan, planExpiresAt, entitlements: {
                                    models: [逻辑名...], features: ["image","tts","browser","subagents"],
                                    maxParallelSubAgents, maxContextTokens },
                                  quota: { used, limit, resetAt }, entitlementVersion }
GET  /account/usage?period=   → 用量明细（设置页用量条数据源）
GET  /account/portal-url      → 托管收银台/管理订阅跳转 URL（带一次性票据）

POST /v1/chat/completions | /v1/embeddings | /v1/images/generations | /v1/audio/speech
     Authorization: Bearer <accessToken>，请求体为 OpenAI 兼容格式，model 填逻辑名
```

**错误码约定**（客户端错误管道按此分流，见 §7.B/C/D）：

| HTTP | `x-athena-code` | 语义 | 客户端行为 |
|---|---|---|---|
| 401 | `token_expired` | 令牌过期 | 单飞刷新后重试一次 |
| 401 | `token_revoked` | 被吊销/别处登录挤下线 | 登出态 + UI 提示，不重试 |
| 402 | `quota_exhausted` | 配额耗尽 | 聊天流内联卡片（升级/加量/切 BYOK） |
| 403 | `feature_not_in_plan` | 档位不含该能力 | 升级卡片；同时触发 entitlement 重拉（防缓存过期） |
| 403 | `plan_suspended` | 退款/风控冻结 | 登出态 + 提示 |
| 429 | `rate_limited` | 限速 | 指数退避重试 ≤2 次；子代理场景转排队 |
| 426 | `app_version_too_old` | 强制升级 kill switch | 提示更新，禁用订阅模式（BYOK 不受影响） |

---

## 4. 数据模型与存储设计

### 4.1 `AppConfig` 变更（最小化）

`config.json` 只新增**一个**字段：

```csharp
// Models/ModelAccessMode.cs（新文件）
public enum ModelAccessMode { Custom, Subscription }

// AppConfig 新增
[ObservableProperty]
private ModelAccessMode _modelAccessMode = ModelAccessMode.Custom;
```

- 默认 `Custom`，旧配置反序列化天然向后兼容，无需迁移函数。
- **刻意不加**任何令牌/邮箱/档位字段到 `AppConfig`——它会被整体 JSON 序列化、被 UI 双向绑定、被 `ConfigChanged` 广播，暴露面太大。

### 4.2 `AthenaData/account.json`（新增，账户状态唯一persist点）

```jsonc
{
  "schemaVersion": 1,
  "email": "user@example.com",
  "refreshToken": "<受保护存储，见下>",
  "entitlementCache": { /* GET /account/me 的最近一次成功响应 */ },
  "entitlementFetchedAt": "2026-07-07T00:00:00Z",
  "lastKnownMode": "Subscription"
}
```

**为什么不放 `config.json`（这是硬依据，不是偏好）**：

1. **旧版本回写丢字段**：`ConfigService.SaveAsync` 用 `JsonSerializer.Serialize(config)` 全量覆盖写。用户若回滚到旧版本 App，旧版本反序列化时忽略未知字段，任何一次保存设置都会**永久丢掉**订阅字段。独立文件对旧版本完全不可见，天然免疫（§7.F4）。
2. **AI 边界**：`view_self_configuration` 返回的是 `AppConfig` 投影，账户数据物理不在其中，就不存在"忘了过滤"这类失误（§7.G）。
3. `lastKnownMode` 用于自愈：若 `config.json` 被旧版本回写导致 `modelAccessMode` 丢失（回落 `Custom`），启动时发现 `account.json.lastKnownMode == Subscription` 且登录态有效，则提示用户"检测到订阅登录，是否恢复订阅模式"，而非静默切换（§7.F1）。

**令牌保护**（v1 方案，务实优先）：

- `accessToken` **只存内存**（`AccountService` 私有字段），进程退出即失；
- `refreshToken` 落盘前用 OS 级保护：Windows `ProtectedData`(DPAPI, CurrentUser)，macOS 优先尝试 Keychain（`security` 无依赖方案：v1 可先用文件 + `chmod 600` + AES(设备指纹派生密钥)，在 §10 标记为已知折衷）；
- `account.json` 纳入 `FileSystemService` 黑名单（与 `config.json` 的自我保护同级），AI 文件工具不可读写。

### 4.3 Entitlement 缓存与离线策略

- 每次成功调用 `/account/me` 全量覆盖缓存并记录时间戳；
- 启动时先用缓存渲染 UI（档位徽章、功能门控），后台静默刷新；
- **宽限期**：缓存超过 72h 未能刷新 → UI 显示"订阅状态待确认"，功能门控降到缓存档位继续放行（模型调用反正需要网络，网关是最终裁决者，客户端门控只是 UX）；缓存超过 14 天 → 门控回落到未登录态；
- `entitlementVersion` 字段用于工具过滤缓存失效（见 §6 Phase 4 的缓存键问题）。

---

## 5. 客户端现状勘察与改造点清单

凭据目前有 **9 条消费路径**，改造必须全覆盖（漏一条 = 订阅模式下该功能静默用 BYOK 凭据，可能烧用户自己的钱，属最高优先级缺陷）：

| # | 路径 | 现状 | 改造 |
|---|---|---|---|
| 1 | `OpenAIChatService.UpdateConfig`（主模型） | **不走 Resolver**，直接读 `_config.BaseUrl/ApiKey` 构造 `OpenAIClient`（L78-85）；`ApiKey` 为空则拒发并提示（L68, L1007） | 改为从统一入口取 `EffectiveModelConfig`；空 Key 校验按模式分流：订阅模式校验"已登录"，文案不同 |
| 2 | `OpenAIEmbeddingService` (L56) | `ModelCredentialResolver.Resolve` | 切统一入口 |
| 3 | `OpenAIImageGenerationService` (L150) | 同上 | 同上 |
| 4 | `ConversationHistoryService` (L88，标题/摘要用次级) | 同上 | 同上 |
| 5 | `SubAgents/SubAgentModelResolver` (L28) | 同上 | 同上 |
| 6 | `Browser/BrowserAgentModelConfig` (L53) | 同上 | 同上 |
| 7 | `KnowledgeMaintenanceModelResolver` (L22) | 同上 | 同上 |
| 8 | `AudioConfigResolver`（TTS，独立解析，BaseUrl 是完整 endpoint `.../audio/speech`，`OpenAIChatService` L1108-1121 直接 HttpClient POST） | 独立逻辑 | 订阅模式返回 `{网关}/v1/audio/speech` + 账户令牌 |
| 9 | `ModelCatalogService`（设置页拉模型列表） | 按 BaseUrl+Key 拉 `/models` | 订阅模式**不调用**；模型下拉替换为 entitlement 中的逻辑模型清单（只读展示档位名） |

其他关联现状：

- `FunctionRegistry.FilterTools`（L544 起）已按 `ImageGenerationEnabled` / `EnableSubAgents` / `DocumentParserEnabled` 三开关过滤工具；`GetToolDeclarationTokenCount` 的缓存键是这三个布尔的元组（L513-523）——**扩展 entitlement 过滤时必须同步扩展缓存键**，否则升降档后 token 估算陈旧。
- `ConfigurationFunctions.AllowedConfigKeys` 白名单（L24-29）不含凭据字段，保持不动；`GetAppConfig` 明确不返回 ApiKey/BaseUrl（L123）。
- `ConfigService` 有成熟的 legacy 迁移模式（`ApplyXxxMigration`）与写时缓存；`ConfigChanged` 事件是模式切换的广播通道。
- Onboarding 是 3 步向导（`OnboardingViewModel.IsStep1/2/3`），含 Provider 下拉与连接测试。
- 本地化：`Assets/Locales/Locale.en-US.axaml` + `Locale.zh-CN.axaml`，UI 文案一律 `{loc:Loc Key}`。
- `TokenService` 是**上下文压力**统计（本地估算 + 供应商 usage 锚定），与订阅配额是两个概念，UI 上严格分开（§7.I1）。

---

## 6. 分阶段实施计划

> 每个 Phase 独立可编译、BYOK 回归通过、可单独 review。Phase 1 是纯重构，甚至可以先单独合回 main 降低分支漂移。

### Phase 0 —— 分支与脚手架（0.5 天）

- [ ] 确认基线：`feature/subscription` 从最新 `main` 拉出。**待确认**：`AthenaStyle` 分支是否先合并回 main？若其改动（主题/文案）会与设置页 UI 冲突，建议先合再拉，避免双向 cherry-pick。
- [ ] 新增 `SubscriptionFeatureEnabled` 开关：读取环境变量 `ATHENA_SUBSCRIPTION_PREVIEW=1` 或 `AthenaData/feature-flags.json`，默认 false。所有新 UI 入口以此门控。
- [ ] 本文档随分支签入，作为实现基准。

**退出标准**：开关关闭时构建产物与 main 行为逐项一致。

### Phase 1 —— 凭据统一入口重构（1~2 天，纯重构，无行为变化）

- [ ] 新增 `Services/Interfaces/IModelEndpointResolver.cs`：
  ```csharp
  public enum ModelRole { Primary, Secondary, Embedding, Image, Audio, BrowserAgent, SubAgent, Maintenance }
  public interface IModelEndpointResolver
  {
      EffectiveModelConfig Resolve(ModelRole role, AppConfig config);
      // Audio 特例：返回完整 endpoint 语义与现状一致
  }
  ```
- [ ] 默认实现 `CustomModelEndpointResolver`：内部原样调用现有 `ModelCredentialResolver` 静态逻辑（含逐字段回退语义），主模型路径也收进来。
- [ ] §5 表中 9 条路径全部改为注入 `IModelEndpointResolver`（DI 注册在 `App.axaml.cs`）。
- [ ] 单测：对每个 Role × (InheritMain/Custom/留空回退) 断言解析结果与重构前一致（可把旧静态类结果作为 golden）。

**退出标准**：BYOK 全功能手测回归（聊天/嵌入/生图/TTS/子代理/浏览器/KB整理/历史摘要/模型列表拉取）无任何行为差异。**此 Phase 建议独立 PR 先合 main**。

### Phase 2 —— 账户服务与登录（3~4 天）

- [ ] `Models/AccountModels.cs`：`AccountState`、`Entitlements`、`QuotaSnapshot`（不可变 record）。
- [ ] `Services/AccountService.cs`（`IAccountService`，DI 单例）：
  - 邮箱验证码登录 / 登出 / 启动时静默恢复会话；
  - **单飞（single-flight）令牌刷新**：并发 401 只发一次 refresh，其余等待共享结果（`SemaphoreSlim` + 刷新结果缓存窗口）；
  - `EntitlementChanged` 事件（档位/配额变化广播，供 UI 与 FilterTools 缓存失效）;
  - 定时（如每 30 分钟 + 每次进设置页）刷新 `/account/me`；
  - 全链路 Serilog 脱敏：令牌一律不落日志。
- [ ] `account.json` 读写（含 §4.2 的 refreshToken 保护、`chmod 600`、损坏容错：解析失败 → 视为未登录并备份坏文件）。
- [ ] `FileSystemService` 黑名单加入 `account.json` 与 `feature-flags.json`。
- [ ] Mock 网关（`Athena.Server.Mock` 本地 ASP.NET Minimal API 或 wiremock 脚本，随分支签入 `Scripts/mock-server/`）：实现 §3.3 全部端点 + 各错误码开关，供无服务端时开发与集成测试。
- [ ] 登录 UI：设置页"账户与订阅"区块骨架（未登录态：邮箱+验证码两步；已登录态：邮箱、档位徽章、登出）。

**退出标准**：对 mock 网关完成 登录→重启恢复→令牌过期自动刷新→吊销后登出 全流程；`account.json` 在旧版本 App 下无副作用（旧版本无视该文件）。

### Phase 3 —— 订阅模式接线（3~4 天）

- [ ] `SubscriptionModelEndpointResolver`：登录态下按 `ModelRole` 返回 `(Provider:"Athena", BaseUrl:网关/v1, ApiKey:accessToken, Model:逻辑名)`；未登录 → 返回明确的"未就绪"结果（**不回退 BYOK**，见 §7.A4）。
- [ ] 组合根：`CompositeModelEndpointResolver` 按 `config.ModelAccessMode` 分发到 Custom/Subscription 实现——模式判定收敛于此一处。
- [ ] accessToken 轮换问题：`OpenAIChatService` 缓存 `OpenAIClient` 实例，令牌刷新后须重建。方案：resolver 返回结果带 `credentialStamp`（令牌哈希），`UpdateConfig`/发送前比对 stamp 决定是否重建 client；`ConfigChanged` 与 `EntitlementChanged` 均触发比对。
- [ ] 错误分流管道：在聊天/嵌入/生图/TTS 的异常处理处识别 §3.3 错误码 → 映射为 `SubscriptionError` 枚举 → 各 UI 按 §7 处置。401 重试逻辑放在最靠近 HTTP 的层，重试**最多一次**。
- [ ] 模式切换：设置页切换 `ModelAccessMode` → `SaveAsync` → `ConfigChanged` 广播 → 各服务重建客户端。进行中的请求不打断（用旧凭据跑完），下一次请求用新模式（§7.A3）。

**退出标准**：对 mock 网关跑通 聊天（流式）/嵌入/生图/TTS/子代理/浏览器/KB整理/历史摘要 全部 8 条路径，抓包确认无任何请求携带 BYOK 凭据；来回切换模式 20 次配置无污染。

### Phase 4 —— 能力门控 EntitlementGate（2~3 天）

- [ ] `FunctionRegistry.FilterTools` 扩展：订阅模式下叠加 entitlement 过滤——`generate_image` 需 `features.image`、`dispatch_subagents` 需 `features.subagents`、`run_browser_task` 需 `features.browser`（BYOK 模式行为不变，仍只看三个配置开关）。
- [ ] **同步扩展 `GetToolDeclarationTokenCount` 缓存键**：由 `(ImageGen, SubAgents, DocParser)` 扩为加上 `(mode, entitlementVersion)`，防升降档后 token 估算陈旧。
- [ ] 数值 clamp（取双方较小值，不改用户配置原值）：
  - `SubAgentMaxParallel` ← `entitlements.maxParallelSubAgents`；
  - `MaxContextTokens` / `CompressionThreshold` ← `entitlements.maxContextTokens`（防用户手动改大后被网关拒，§7.D4）；
  - clamp 发生在读取点（resolver/orchestrator），配置文件里的用户值原样保留，切回 BYOK 即恢复。
- [ ] TTS：档位无 `features.tts` 时，设置页音频区显示升级提示且 `ChatAudioEnabled` 面板置灰（不改配置值本身）。
- [ ] 被门控功能的**调用兜底**：即使工具被过滤，若模型仍生成了对被禁工具的调用（历史会话恢复等边角），`FunctionRegistry.ExecuteAsync` 返回带升级引导的失败结果而非裸异常。

**退出标准**：mock 网关切换档位 → 工具列表、并行数、上下文上限即时正确；档位徽章与工具可用性一致。

### Phase 5 —— 设置页改造（2~3 天）

- [ ] "账户与订阅"区块置顶：登录态、档位徽章（本地化档位名）、配额用量条（数据源 `/account/usage`，标注刷新时间）、[管理订阅]（打开 `portal-url`）、[升级]。
- [ ] 模式切换控件（单选：订阅托管 / 自备 Key）：
  - 切到订阅但未登录 → 引导登录，登录成功才真正落 `ModelAccessMode`；
  - 切回 BYOK → BYOK 字段原样恢复（它们从未被改过）。
- [ ] 订阅模式下：现有模型配置区整体折叠为只读档位说明（显示逻辑角色名："主模型：奥林匹斯档"），Provider/BaseUrl/ApiKey/模型下拉/拉取模型列表按钮全部隐藏；BYOK 模式下界面与现状**完全一致**。
- [ ] 用量条与 `TokenService` 上下文压力条视觉区分（不同图标+文案），避免用户混淆两个概念。
- [ ] 全部新文案进两份 Locale 文件。

**退出标准**：两种模式下设置页截图对比评审通过；`SubscriptionFeatureEnabled=false` 时设置页与 main 逐像素一致。

### Phase 6 —— Onboarding 分叉与场景化升级卡（2 天）

- [ ] Onboarding 第 2 步（现 API 配置步）前插入模式选择页："订阅托管，登录即用（推荐）" vs "自备 API Key，完全免费"；选订阅 → 走登录流程；选 BYOK → 现有流程原样。
- [ ] 聊天流内联升级卡片（复用现有消息段/卡片机制）：`402 quota_exhausted`、`403 feature_not_in_plan` 时渲染，含升级按钮与"切换自备 Key"链接；同类卡片 10 分钟内去重（§7.J3）。
- [ ] 配额 80% / 95% 预警：输入框上方细条提示，每周期各提醒一次。

**退出标准**：新装用户两条路径均可顺利完成首轮对话；升级卡不会刷屏。

### Phase 7 —— 服务端 MVP（独立仓库 `athena-server`，与 Phase 2-6 并行）

> 本仓库计划只锁定契约（§3.3）与里程碑；服务端实现细节在 `athena-server` 自己的计划文档中展开。

- [ ] M1：网关选型验证（new-api vs LiteLLM Proxy，见 §10）——虚拟 Key、配额、模型映射、usage 上报四项能力 PoC；
- [ ] M2：BFF 账户服务（邮箱 OTP、JWT、设备记录、entitlement 组装）+ 网关虚拟 Key 生命周期同步（订阅变更 → 调整虚拟 Key 额度/模型组）；
- [ ] M3：支付渠道接入（**待决策**，§10）+ webhook（成功/退款/到期）驱动订阅状态机：`active → grace(7d) → expired`，退款 → `suspended`；
- [ ] M4：滥用防护：按账户限速、单账户并发上限、异常用量告警、令牌设备绑定校验；
- [ ] M5：kill switch（`app_version_too_old`）与按档位的模型映射后台。

**退出标准**：真实网关替换 mock，客户端 Phase 3/4 的退出标准复测通过。

### Phase 8 —— 端到端验收、文档与灰度（2~3 天）

- [ ] §8 测试矩阵全量执行；
- [ ] `Docs/PrivacyPolicy.md` 修订（订阅模式下对话内容经我方服务器转发的披露 + BYOK 不经过的承诺保留）；用户指南补订阅章节（中英）；
- [ ] `CLAUDE.md` / `AGENTS.md` 补充订阅架构摘要；
- [ ] 灰度：feature flag 默认关发一版（代码合入但不可见）→ 内测名单开 flag → 服务端观察一周 → 正式版默认开。

---

## 7. 边界情况清单（评审重点）

> 每条：场景 → 预期行为 → 实现落点。标 ⚠ 的是容易漏、后果重的。

### A. 模式切换与共存

| # | 场景 | 预期行为 | 落点 |
|---|---|---|---|
| A1 | 对话流式进行中切换模式 | 当前轮用旧凭据跑完，下一轮生效新模式；不中断不报错 | Phase 3 credentialStamp 比对时机=发送前 |
| A2 | 双模式配置污染 | 订阅模式运行期间 BYOK 的 9 组凭据字段一个字节都不变；来回切换 N 次后 `config.json` 除 `modelAccessMode` 外无 diff | Phase 3 退出标准（自动化：切换前后 config 快照比对） |
| A3 | 切换时后台任务在跑（KB 整理 / 定时任务触发的对话 / 子代理批次） | 任务开始时解析的凭据快照用到底；下次任务用新模式 | 各 orchestrator 已是"启动时解析"语义，Phase 1 保持 |
| A4 ⚠ | 订阅模式但未登录/登录失效 | **短路报错并引导登录，绝不静默回退 BYOK**（防止在用户不知情时消耗其自己的 Key 余额） | `SubscriptionModelEndpointResolver` 返回未就绪；聊天输入区 banner |
| A5 | 订阅模式下点"拉取模型列表" | 该按钮不存在（UI 隐藏）；模型下拉替换为档位说明 | Phase 5 |
| A6 | `SubscriptionFeatureEnabled=false` 但 `config.ModelAccessMode==Subscription`（内测用户关回开关） | 视为 `Custom` 处理并在日志警告，UI 不出现订阅痕迹 | `CompositeResolver` 判定处兜底 |

### B. 认证与会话

| # | 场景 | 预期行为 | 落点 |
|---|---|---|---|
| B1 | accessToken 对话中途过期 | 401 → 单飞刷新 → 原请求重试一次；再失败按 B4 | Phase 3 错误管道 |
| B2 ⚠ | 并发 401 风暴（聊天+嵌入+N 个子代理同时打） | 只发一次 refresh；其余调用等待共享结果；refresh 失败则全部快速失败，不产生 N 次 refresh | `AccountService` single-flight |
| B3 | 流式响应中途连接被网关掐断（令牌吊销/配额判定） | 本轮标记失败可重试；UI 显示已收到的部分 + 错误条 | 复用现有流式异常路径，映射 `SubscriptionError` |
| B4 | refreshToken 失效/吊销/别处登录挤下线 | 进入"需重新登录"态：暂停 KB 整理与定时任务的订阅调用、聊天入口 banner、保留邮箱显示便于重登 | `AccountService` 状态机 + `EntitlementChanged` |
| B5 | 本机时钟偏移导致 JWT 本地过期判断失真 | 过期判断只信服务端 401，本地 exp 仅用于"提前刷新"优化（提前 5 分钟），不做拒发依据 | `AccountService` |
| B6 | 登出时子代理批次/浏览器任务进行中 | 弹确认："有任务进行中，登出将中断"；确认后取消批次（复用现有取消机制）再清令牌 | Phase 5 登出命令 |
| B7 | `account.json` 损坏/被手工改坏 | 解析失败 → 备份为 `.corrupt` → 视为未登录，不崩溃 | Phase 2 读入容错 |
| B8 | 多开应用实例（同目录） | v1 不支持并发写：`account.json` 写入用原子替换（tmp+rename），后写覆盖先写；不做锁 | Phase 2，风险登记 §10 |

### C. 网络与离线

| # | 场景 | 预期行为 | 落点 |
|---|---|---|---|
| C1 | 离线启动 | 用 entitlement 缓存渲染档位/门控（≤72h 完全正常，≤14d 显示"待确认"），登录态保持；发消息时网络错误正常报 | §4.3 |
| C2 ⚠ | 区分"网关不可达"与"认证失败" | 网络层异常（DNS/超时/5xx）→ 保持登录态，提示检查网络，**不**触发登出；仅 401/403 走认证流程 | 错误管道分流：传输异常 vs HTTP 状态 |
| C3 | 支付后回跳 | 点升级 → 开浏览器 → 客户端进入 90s 轮询 `/account/me`；成功 → 档位即时更新 + 祝贺 toast；超时 → 显示"已完成支付？点此刷新" | Phase 5/6 |
| C4 | 代理/防火墙环境 | 网关请求走系统代理（HttpClient 默认行为，与现有 OpenAI SDK 一致），不引入自定义代理配置 | 无需改动，测试矩阵覆盖 |

### D. 配额与限流

| # | 场景 | 预期行为 | 落点 |
|---|---|---|---|
| D1 | 配额在多轮对话间耗尽 | 网关受理时判定（不在流中间截断）；下一轮 402 → 内联升级卡；已在跑的子代理批次各自失败但不连坐主对话 | 服务端 M3；客户端错误管道 |
| D2 | 429 限速 | 指数退避重试 ≤2 次；子代理并发遇 429 → 该子代理转排队重试（复用 `SubAgentMaxParallel` 队列），批次不整体失败 | Phase 3/4 |
| D3 | 配额周期重置 | 以服务端 `resetAt`(UTC) 为准，UI 转本地时间显示"将于 X 重置" | Phase 5 |
| D4 ⚠ | 用户把 `MaxContextTokens` 手动改超档位上限 | 读取点 clamp 到档位上限，配置原值保留；压缩阈值联动 clamp，保证 `threshold < max` 不被破坏 | Phase 4 |
| D5 | 单次超大请求（长附件解析后超上下文） | 现有 TokenService 压力机制先行预警；网关拒绝时错误文案指向"上下文超出档位上限"而非泛化报错 | 错误码映射 |
| D6 | 生图/TTS 按次计量与 token 配额并存 | 网关统一折算为积分（credits）记账，客户端只展示网关返回的 used/limit，不做本地折算 | 契约 §3.3 |

### E. 订阅生命周期

| # | 场景 | 预期行为 | 落点 |
|---|---|---|---|
| E1 | 升级立即生效 | 支付回跳轮询成功 → `EntitlementChanged` → FilterTools 缓存失效、clamp 值更新、徽章更新，无需重启 | Phase 4 缓存键含 entitlementVersion |
| E2 | 降级/到期 | 7 天宽限期（服务端状态机）；宽限内档位不变但 UI 黄条提醒；到期后 entitlement 降档，进行中任务跑完、新调用按新档位 | 服务端 M3 + 客户端徽章 |
| E3 | 退款/chargeback | 服务端置 `suspended` → 客户端 403 `plan_suspended` → 登出态+客服指引文案 | 错误管道 |
| E4 | 同一账户多设备 | v1 允许 ≤3 设备（服务端记录 device），超限时最旧设备被挤下线（B4 路径） | 服务端 M2 |
| E5 | 免费试用 | **待决策**（§10）：若做，试用也走正常 entitlement，无特殊代码路径 | — |

### F. 配置兼容与版本升降级

| # | 场景 | 预期行为 | 落点 |
|---|---|---|---|
| F1 ⚠ | 用户回滚旧版本 App，旧版本保存设置把 `modelAccessMode` 字段抹掉，再升级回新版本 | 启动自愈：`account.json.lastKnownMode==Subscription` 且登录有效 → 弹一次性确认"恢复订阅模式？"；拒绝则改写 `lastKnownMode` 不再问 | §4.2 |
| F2 | 全新安装 | 默认 `Custom` + 未登录，与今日行为一致；Onboarding 出现分叉页（flag 开启时） | Phase 6 |
| F3 | 从含订阅的配置导出/拷贝 `AthenaData` 到另一台机器 | `refreshToken` 因 DPAPI/设备派生密钥不可解 → 视为未登录，要求重登；不崩溃 | §4.2 令牌保护副作用，测试覆盖 |
| F4 | 旧版本 App 看到 `account.json` | 完全无感知、不读不写 | 独立文件设计使然 |
| F5 | in-app 更新（`Athena.Updater`）跨订阅版本 | `AthenaData` 不在更新替换范围内（现有行为），账户态自然保留；更新后首启刷新 entitlement | 验证即可，无改动 |

### G. AI 自我配置与安全边界

| # | 场景 | 预期行为 | 落点 |
|---|---|---|---|
| G1 ⚠ | AI 通过 `modify_self_configuration` 改 `ModelAccessMode` 或账户字段 | 不可能：白名单 `AllowedConfigKeys` 不加任何新键；显式测试断言 | Phase 2 单测 |
| G2 | AI 通过 `view_self_configuration` 看到令牌/邮箱 | 不可能：账户数据不在 `AppConfig`；可选地在返回中加只读 `AccessMode`/`PlanName`（帮助 AI 向用户解释功能不可用原因），但绝无 PII | Phase 4 |
| G3 ⚠ | AI 文件工具读写 `account.json` / `feature-flags.json` | `FileSystemService` 黑名单拦截（与 `config.json` 自保护同级） | Phase 2 |
| G4 | 令牌进日志 | `AccountService` 全链路脱敏；review checklist：任何新增 `Log.*` 不得输出 Authorization/token 字段；现有 L82 打印 BaseUrl 在订阅模式打印的是网关域名，无敏感 | Phase 2/3 |
| G5 | 子代理/KB 维护路径的工具门控 | entitlement 过滤在 `FilterTools` 统一生效，三条执行路径（主聊天/子代理/KB维护）共用 `FunctionRegistry`，天然覆盖 | Phase 4 |

### H. 后台任务与并发

| # | 场景 | 预期行为 | 落点 |
|---|---|---|---|
| H1 | 定时任务在配额耗尽/登出时触发 | 执行失败 → 任务标记失败并在任务页可见 + 系统通知一次，不静默丢、不无限重试 | 复用 `TaskScheduler` 失败路径，文案区分订阅原因 |
| H2 | KB 整理周期任务遇 402/403 | 本轮跳过 + 日志 + 退避到下个周期；不弹窗打扰 | `KnowledgeBaseMaintenanceRunner` 异常分支 |
| H3 | 嵌入调用失败（KB 写入的同步持久化承诺） | 保持现状语义：`create_new_memory` 显式报错，绝不静默丢向量；错误文案带订阅原因 | 错误管道透传 |
| H4 | 令牌刷新恰逢子代理批次启动 | 子代理启动前统一取一次凭据快照（经 single-flight 保证拿到的是刷新后令牌） | Phase 3 |

### I. 计量一致性

| # | 场景 | 预期行为 | 落点 |
|---|---|---|---|
| I1 ⚠ | 用户混淆"上下文压力条"（TokenService）与"配额用量条" | 两个控件视觉/文案/位置严格区分；配额条只出现在设置页与预警条 | Phase 5 |
| I2 | 网关 usage 上报延迟 | 用量条标注"更新于 X 分钟前"，最终一致即可；不做本地累加拟合 | Phase 5 |
| I3 | 客户端 token 估算与网关计费差异引发投诉 | 计费只以网关为准并在订阅协议页写明；客户端不展示"预计费用" | 文档/文案约束 |

### J. UI 与本地化

| # | 场景 | 预期行为 | 落点 |
|---|---|---|---|
| J1 | 中英文案 | 所有新 key 双语齐全；档位名走 loc key（如 信使/智者/奥林匹斯 ↔ Hermes/Sage/Olympus） | 每个 UI Phase 的 DoD |
| J2 | 升级卡片视觉 | 复用现有卡片/段落机制与主题 token，暗/亮主题各验一遍（Semi 资源键陷阱见项目记忆） | Phase 6 |
| J3 | 提示疲劳 | 同类升级卡 10 分钟去重；配额预警每周期每档一次 | Phase 6 |
| J4 | 窗口语言运行时切换 | 账户区块文案跟随现有 `LocalizationService` 机制即时切换 | Phase 5 |

---

## 8. 测试计划

### 8.1 单元测试（新增 `Athena.Subscription.Tests` 项目，模式参照 `Athena.Archive.Tests`）

- Resolver 矩阵：`ModelRole × ModelAccessMode × 登录态 × (InheritMain/Custom/留空回退)` 全组合断言；重点断言 A2（BYOK 字段零污染）与 A4（未登录短路）。
- `AccountService`：single-flight 并发刷新（100 并发只 1 次 refresh）、B5 提前刷新窗口、B7 损坏文件容错、令牌脱敏（断言日志 sink 无 token 字符串）。
- 门控：FilterTools 的 entitlement 过滤 + 缓存键失效（换 entitlementVersion 后 token 计数变化）；G1/G2 白名单与投影断言。
- clamp：D4 的取小值与阈值联动。
- 自愈：F1 的 `lastKnownMode` 恢复询问逻辑。

### 8.2 集成测试（对 mock 网关）

脚本化跑通：登录 → 流式聊天 → 强制 401（mock 开关）→ 自动刷新续流 → 强制 402 → 升级卡 → mock 升档 → 工具列表扩展 → 登出。

### 8.3 手动测试矩阵（Phase 8 执行）

| 维度 | 取值 |
|---|---|
| 模式 | BYOK / 订阅-免费档 / 订阅-基础档 / 订阅-专业档 |
| 网络 | 正常 / 断网 / 网关 5xx / 高延迟 |
| 令牌 | 有效 / 过期可刷新 / 吊销 / 挤下线 |
| 配额 | 充足 / 80% / 耗尽 |
| 平台 | Windows / macOS |
| 语言 | zh-CN / en-US |
| 功能抽样 | 流式聊天+工具调用 / 子代理批次 / 浏览器任务 / KB 写入+检索 / 生图 / TTS / 定时任务触发 / 长对话压缩 / 历史摘要 / Rewind-Fork |

BYOK 回归清单单列：上述功能抽样在 BYOK 模式下与 main 版本逐项对比，**必须零差异**。

---

## 9. 发布与灰度

1. **合并节奏**：Phase 1（纯重构）独立 PR 先行合 main；Phase 2-6 在 `feature/subscription` 累积，flag 默认关，可分批合入；
2. **服务端先行**：athena-server 生产环境先稳定运行（自用+内测账户）一周；
3. **内测**：向少量用户发放开启 flag 的构建（或远程 flag），收集 §7 边界情况的真实触发；
4. **正式**：默认开 flag 的版本走现有 GitHub Release + in-app 更新通道；
5. **Kill switch**：服务端可下发 `app_version_too_old`/维护模式，只禁订阅路径，BYOK 永不受影响——这是最终兜底：任何服务端事故下，用户切回 BYOK 应用完全可用。

---

## 10. 待决策项与风险登记

| # | 事项 | 选项/建议 | 状态 |
|---|---|---|---|
| R1 | 支付渠道 | 海外：Paddle / Lemon Squeezy（商户记录，免税务负担）；国内：需公司主体或聚合支付。**建议先只上海外渠道验证需求** | 待拍板 |
| R2 | 网关选型 | new-api（国内生态、多供应商、虚拟 Key 成熟）vs LiteLLM Proxy（社区大、Python）。PoC 后定 | Phase 7 M1 |
| R3 | 合规 | 面向国内售卖转发境外模型有监管风险；建议国内区档位映射到国产模型（DeepSeek/Qwen/GLM），逻辑模型名机制天然支持按区路由 | 待法务确认 |
| R4 | 定价与档位 | 三档草案（自备Key免费 / 基础 / 专业），具体价格与配额需成本测算（子代理与浏览器视觉是成本大头） | 待测算 |
| R5 | macOS 令牌存储 | v1 用文件+设备派生密钥（非 Keychain），存在本机恶意进程读取风险；v2 评估 Keychain P/Invoke | 已知折衷 |
| R6 | 多实例并发写 account.json | v1 原子替换不加锁；损坏概率低且有容错。若内测出现问题再上文件锁 | 已知折衷 |
| R7 | 免费试用 | 倾向 v1 不做（防滥用成本高），以免费档=BYOK 承接 | 待拍板 |
| R8 | 嵌入模型锁定 | `athena-embedding` 底层模型上线后冻结；更换需 `-v2` 新逻辑名+重建索引流程（单独立项） | 设计约束 |

---

## 11. 工作量汇总（客户端）

| Phase | 内容 | 预估 |
|---|---|---|
| 0 | 分支/flag 脚手架 | 0.5d |
| 1 | 凭据统一入口重构（可先合 main） | 1~2d |
| 2 | 账户服务+登录+mock 网关 | 3~4d |
| 3 | 订阅模式接线+错误管道 | 3~4d |
| 4 | 能力门控 | 2~3d |
| 5 | 设置页 | 2~3d |
| 6 | Onboarding+升级卡 | 2d |
| 8 | 联调验收+文档 | 2~3d |
| **合计** | | **16~22 人日**（服务端 Phase 7 另计，可并行） |
