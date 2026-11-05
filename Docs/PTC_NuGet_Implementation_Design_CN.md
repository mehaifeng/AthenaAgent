# 通用 .NET PTC 组件设计与 NuGet 原型实施指南

> 状态：待实施设计基线；本文的“必须”是后续实现与验收要求，不代表代码已经完成。
>
> 日期：2026-11-04。首个消费者：AthenaAgent，目标框架 `net10.0`。
>
> 交付对象：负责组件、打包及 Athena 适配的实施 Agent。
>
> 当前交付范围：设计文档。后续原型的完成条件是“组件测试通过、真实 NuGet 消费通过、Athena 工具循环适配通过”，公开发布包不在原型验收范围内。

## 1. 实施目标与执行入口

实现可由不同 .NET Agent 使用的 PTC 执行与工具编排层。宿主负责模型请求、会话、业务工具、授权和呈现；组件接收模型生成的 JavaScript，执行条件、循环、并发和结果筛选，经宿主调用工具，再返回有限的模型输出及完整执行记录。

模型仍可通过普通 function calling 调用 `execute_program`，组件自身不请求模型，也不要求供应商提供专用 PTC API。模型生成代码的可靠性需要另外评测，不能由解释器可运行推出。

实施 Agent 从下面的任务说明开始：

> 阅读本设计、根目录 AGENTS.md，以及将要修改目录中的 CLAUDE.md。先核对当前代码与本文列出的入口是否一致，再按 M0 至 M6 顺序实现。先完成独立组件和包消费验证，再接 Athena。每个阶段提交可复现命令、退出码与失败用例证据。Athena 首版默认关闭，只开放明确授权的只读工具。包可打包、包被真实安装、工具循环通过和真实模型完成任务必须分别报告。不得将本设计或临时验证当成已实现功能；不得据此发布 NuGet、推送、部署或修改用户凭据。

### 1.1 原型范围

- 两个 NuGet 包：`Ptc.Core`、`Ptc.JavaScript`，均为暂定 ID；实施 M0 时检查名称冲突并固定前缀。
- 宿主提供工具描述、JSON Schema、实际调用入口及调度判断。
- JavaScript 支持局部变量、条件、循环、`async/await`、`Promise.all` 与 `Promise.allSettled`。
- 工具数据只通过受限 JSON 边界传递；支持附件引用，不把宿主业务对象交给脚本。
- 每次执行的授权快照、调用数、时间、输出、数据量、并发和待处理调用数均有限额。
- 执行事件、可查询的原始工具记录，以及单独构造的模型结果投影。
- 一个独立 .NET 消费者和一个 Athena 适配器，均实际消费生成的包。

首版采用 `net10.0`，验证后再按消费者需要增加多目标框架。Python、持久化解释器状态、后台程序、跨进程断点续跑、自动代码修复、跨工具事务和自动重放属于后续扩展。`Ptc.Extensions.AI` 可作为后续可选包，不作为首版打包依赖。

### 1.2 已有可行性证据

2026-11-04 在当前 Linux 工作环境，以 SDK `10.0.113`、运行时 `10.0.13`、Jint `4.17.0` 跑过临时控制台验证，七项断言通过：

| 检查 | 已观察结果 | 证据边界 |
|---|---|---|
| 依赖链及筛选 | 先取得 ID，再取详情，仅输出 `[1,3]` | 合成数据与本地回调 |
| 异步并发 | `Promise.all` 中三个宿主回调同时在途 | 不是对 Jint 的多线程并发访问 |
| 上下文 | 回调观察到同一个 AsyncLocal 测试标识 | 尚未覆盖 Athena 的审批作用域和多 Run 隔离 |
| 未授权调用 | 适配回调拒绝未列入名单的工具 | 尚未实现组件的统一授权闸门 |
| 默认能力 | `System`、`require`、`fetch` 全局不存在 | 不是沙箱逃逸测试或安全证明 |
| 无限循环 | 语句数约束中断 `while (true) {}` | 尚未覆盖全部异步恢复路径 |
| 取消 | 取消到达异步回调与解释器执行 | 回调主动遵守 CancellationToken |

临时目录不能作为后续 Agent 的前置依赖。M1、M2 必须把对应能力落实为仓库内可复现测试。上述验证没有完成 NuGet 封装、Athena 接入或跨平台认证。

## 2. 包边界与目录安排

### 2.1 职责划分

| 模块 | 负责 | 依赖边界 |
|---|---|---|
| `Ptc.Core` | 公共契约、授权与预算协调、调用调度、执行状态、事件和模型结果投影 | .NET 基础库与 System.Text.Json |
| `Ptc.JavaScript` | Jint 生命周期、脚本环境、Promise 桥接及解释器约束 | Ptc.Core、固定版本 Jint |
| 独立示例宿主 | 合成业务工具、权限策略与事件接收 | 通过 PackageReference 安装两个包 |
| Athena 适配 | 工具快照、审批/会话作用域、现有 Registry 调用、事件、附件和本地记录 | 留在 Athena，不打入通用包 |

两个包不得引用 Athena.UI、Avalonia、具体模型 SDK、MCP SDK 或 Serilog。宿主通过适配器提供这些能力。通用包使用 MIT 作为拟定许可证；Jint 的 BSD-2-Clause 许可与第三方声明独立保留。引入其他依赖时核对其实际许可证。

### 2.2 推荐源码布局

以下为待创建路径，不是现有文件清单：

~~~text
Libraries/Ptc/
  Ptc.slnx
  Directory.Build.props
  README.md
  LICENSE
  ThirdPartyNotices.md
  src/
    Ptc.Core/
    Ptc.JavaScript/
  tests/
    Ptc.Core.Tests/
    Ptc.JavaScript.Tests/
  samples/
    StandaloneHost/
    PackageConsumer/
  scripts/
    verify-package-consumer.sh
~~~

独立维护 `Ptc.slnx`，包的构建与测试不得依赖 Athena 主工程。测试建议采用 xUnit；选择实施时可用的稳定依赖并固定版本。首版 Jint 基线固定 `4.17.0`，升级需要重新运行兼容与限制测试。

Ptc.slnx 首版只包含 src 和 tests。StandaloneHost 可在开发时使用 ProjectReference，但不作为包消费验收证据；PackageConsumer 只保留 PackageReference，在 M3 生成 feed 后复制到仓库外还原。不要把依赖尚未生成包的消费者加入首次 solution restore。

**先处理主工程编译范围，再创建 C# 文件。** Athena 主项目位于仓库根目录，SDK 默认递归包含 `*.cs`。M0 必须在 `Athena.UI.csproj` 增加：

~~~xml
<Compile Remove="Libraries/**/*.cs" />
~~~

否则组件、示例及测试源码会被误编入桌面应用。不要通过关闭整个项目的默认编译项来修复。其他根目录构建项如有同类包含规则，也应核对实际影响。

首版不必把组件工程加入 Athena 主解决方案；独立 solution 和包消费验证先成立。Athena 最终通过 PackageReference 使用本地源中的包，不能以 ProjectReference 作为打包验收证据。

## 3. 系统结构与信任边界

~~~mermaid
flowchart TD
    A[宿主 Agent 的模型工具循环] --> B[execute_program]
    B --> C[PTC 执行协调器]
    C --> D[每次独立的 JavaScript 引擎]
    D --> E[JSON 工具桥接]
    E --> F[本次授权和预算检查]
    F --> G[有界调用调度]
    G --> H[宿主参数校验和逐项审批]
    H --> I[实际工具]
    I --> E
    C --> J[完整工具记录和执行事件]
    C --> K[有限的模型结果投影]
    K --> A
~~~

代码、工具名和参数均由模型提供，属于不可信输入。允许工具集合、预算、宿主上下文和运行时配置由宿主创建，脚本不能提交或修改。工具描述和结果也按数据处理，不执行其中的“指令”。

### 3.1 运行时边界

- 每个 Run 使用独立引擎和独立预算；V0 不复用可变 JavaScript 全局状态。
- 不开启 CLR 类型/程序集加载，不注册文件、进程、网络或通用模块加载器。
- 脚本只获得 `tools`、`text`、`inputs` 与必要的标准语言能力。
- 桥接仅交换经过验证的 JSON；禁止传入 ServiceProvider、委托容器、业务对象、原始 Task 或可反射的 .NET 返回值。
- 使用固定版本引擎支持的配置限制反射和字符串代码编译，并测试可达路径。删除全局名称或 AST 黑名单不能作为完整安全隔离证明。
- Jint 引擎实例不并发访问。宿主 I/O 可以并发，回调完成时不得从多个线程操作 Engine 或 JsValue。
- `TaskInterop` 属于需要显式启用与验证的桥接机制，不能凭支持 `await` 推出任意异步宿主对象都可安全暴露。
- 进程内资源约束不等同于进程或操作系统级隔离。V0 在结果元数据中标明 `isolation: "inProcess"`；公开文档不能称其已提供强沙箱。
- 后续独立 Worker 可以改善故障隔离；文件、网络和凭据隔离仍需要明确的操作系统策略，单独启动进程不自动具备这些限制。

### 3.2 权限与调度顺序

一次调用按以下顺序处理，所有实际工具必须走同一路径：

1. 检查 Run 是否已取消、超时或触发不可恢复的预算停止。
2. 原子计入本次尝试数，检查名称/参数字节数和待处理数量。
3. 核对名称属于宿主快照与本次 AllowedTools 的交集；拒绝递归 `execute_program`。
4. 验证参数是 JSON 对象，且满足对应 Schema。
5. 取得宿主调度判断并排队；等待期间仍受取消和总时限约束。
6. 开始前再次检查停止状态与实际执行授权，进入宿主的逐项审批和调用入口。
7. 记录真实结果、附件引用及终态，完成数据量检查，再交回脚本。
8. 通过独立事件通道通知宿主；终态归并后构造模型结果。

工具清单中没有的名字，即使真实 Registry 存在，也不得执行。工具的风险标签只供描述；不能取代宿主的实际授权。通用组件验证 Schema 的实现可以独立于 Athena，但 Athena 原有 Registry 校验仍保留。

## 4. 公共 API 基线

下面是可编译的契约骨架，定义责任与数据形状；执行器、投影器及工厂的实现由后续阶段补齐。允许在实现前细化命名，必须同步示例、测试和文档。不要在消费者接入后无记录地修改公共协议。

~~~csharp
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Ptc.Core;

public enum PtcScheduling { Serial, ParallelReadOnly }
public enum PtcRunStatus { Completed, Rejected, Failed, Cancelled, TimedOut, BudgetExceeded }
public enum PtcCallState { Queued, Running, Succeeded, Failed, Denied, Cancelled, NotStarted, OutcomeUnknown }

public sealed record PtcToolDefinition(
    string Name, string Description, JsonElement InputSchema);

public sealed record PtcRunContext(
    string RunId, string ParentToolCallId, string? ConversationId,
    string? WorkspaceId, string? DelegatedTask);

public sealed record PtcExecutionRequest(
    string Code, JsonElement Inputs, IReadOnlySet<string> AllowedTools,
    PtcRunContext Context, PtcExecutionLimits Limits);

public sealed record PtcExecutionLimits
{
    public int MaxCodeChars { get; init; } = 32_000;
    public long MaxStatements { get; init; } = 100_000;
    public int MaxRecursionDepth { get; init; } = 64;
    public long EngineMemoryBytes { get; init; } = 32 * 1024 * 1024;
    public TimeSpan MaxWallTime { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan MaxToolTime { get; init; } = TimeSpan.FromSeconds(10);
    public int MaxToolCalls { get; init; } = 64;
    public int MaxConcurrency { get; init; } = 4;
    public int MaxPendingCalls { get; init; } = 64;
    public int MaxJsonDepth { get; init; } = 32;
    public int MaxInputBytes { get; init; } = 128 * 1024;
    public int MaxArgumentsBytes { get; init; } = 64 * 1024;
    public int MaxToolResultBytes { get; init; } = 512 * 1024;
    public long MaxRunDataBytes { get; init; } = 8 * 1024 * 1024;
    public int MaxOutputBytes { get; init; } = 16 * 1024;
    public int MaxModelResultBytes { get; init; } = 24 * 1024;
    public int MaxArtifactReferences { get; init; } = 32;
    public TimeSpan DrainTimeout { get; init; } = TimeSpan.FromSeconds(2);
}

public sealed record PtcToolCall(string CallId, string Name, JsonElement Arguments);
public sealed record PtcArtifactReference(string Id, string MediaType, string DisplayName);
public sealed record PtcToolResult(
    bool Success, string Message, JsonElement? Data, string? ErrorCode,
    IReadOnlyList<PtcArtifactReference> Artifacts);

public sealed record PtcFailure(string Code, string Message);
public sealed record PtcToolRecord(
    PtcToolCall Call, PtcCallState State, PtcToolResult? Result, TimeSpan Duration);

public sealed record PtcRunMetrics(
    int CallsAttempted, int CallsStarted, int CallsFinished,
    long ToolResultBytes, int OutputBytes, int PeakConcurrency, TimeSpan Duration);

public sealed record PtcExecutionResult(
    PtcRunContext Context, PtcRunStatus Status, IReadOnlyList<JsonElement> Output,
    IReadOnlyList<PtcToolRecord> ToolRecords, IReadOnlyList<PtcArtifactReference> Artifacts,
    PtcRunMetrics Metrics, PtcFailure? Failure);

public sealed record PtcEvent(
    string RunId, string ParentToolCallId, long Sequence,
    string Kind, string? CallId, string? ToolName, string? ErrorCode);

public interface IPtcToolHost
{
    IReadOnlyList<PtcToolDefinition> Tools { get; }
    PtcScheduling GetScheduling(PtcToolCall call, PtcRunContext context);
    ValueTask<PtcToolResult> InvokeAsync(
        PtcToolCall call, PtcRunContext context, CancellationToken cancellationToken);
}

public interface IPtcEventSink
{
    ValueTask OnEventAsync(PtcEvent item, CancellationToken cancellationToken);
}

public interface IPtcScriptBridge
{
    IReadOnlyList<PtcToolDefinition> Tools { get; }
    ValueTask<PtcToolResult> CallAsync(
        string name, JsonElement arguments, CancellationToken cancellationToken);
    void Emit(JsonElement value);
}

public interface IPtcScriptRuntime
{
    ValueTask RunAsync(
        string code, JsonElement inputs, IPtcScriptBridge bridge,
        PtcExecutionLimits limits, CancellationToken cancellationToken);
}

public interface IPtcExecutor
{
    Task<PtcExecutionResult> ExecuteAsync(
        PtcExecutionRequest request, IPtcToolHost host,
        IPtcEventSink? events = null, CancellationToken cancellationToken = default);
}
~~~

Core 中的 PtcExecutor 实现协调器并依赖 IPtcScriptRuntime；JavaScript 包实现 JintScriptRuntime。每个 Run 创建独立 IPtcScriptBridge，由它统一执行授权、预算、调度、事件与输出收集。运行时只能经此桥调用工具，不能取得 IPtcToolHost 后绕过协调器。将来替换 Worker 或解释器时沿用相同桥接契约。

CPU 解释执行安排在宿主 UI 线程之外；异步 I/O 不同步阻塞等待。引擎由一个串行执行所有者推进，不能对同一 Engine 同时 Task.Run 多段脚本。宿主上下文仍在实际工具调用点恢复，不依靠调度线程碰巧继承的状态。

### 4.1 数据所有权

- Request、工具清单、Schema、AllowedTools 和 Limits 在 Run 开始时复制为不可变快照，名称按 `StringComparer.Ordinal` 比较。
- 所有跨边界 JsonElement 在其 JsonDocument 被释放前 Clone；不得借用宿主短生命周期的文档。
- `GetScheduling` 不做审批或执行，只说明这次调用是否一定可以无弹窗地只读并发。默认 Serial。
- `InvokeAsync` 负责宿主实际授权、校验、审批和业务调用，不得因外层脚本已获批而跳过。
- 动态权限撤销在实际执行点生效；不确定是否仍免审批时应串行，不能强制使用已过期的并发判断。
- 运行过程中发生的取消、超时和执行失败归并到 PtcExecutionResult。非法配置、缺失依赖等调用方错误在执行任何工具前明确抛出。
- `PtcEvent` 是元数据事件，不含原始结果或秘密参数；需要完整记录时读取 ToolRecords，由宿主决定如何持久化。

### 4.2 Schema 能力与目录额度

工具目录也必须有限：V0 默认最多 128 个定义，序列化目录最多 512 KiB；工具名 1 至 256 字符。超出时宿主缩小本次快照，不能悄悄只授权目录前半段。目录元数据与输入在进入解释器前计量，原始调用参数与结果均计入 MaxRunDataBytes；模型输出另计。

Schema 验证必须声明支持范围。首版至少覆盖 type、properties、required、additionalProperties、items、enum、字符串长度、数值上下界及数组长度。选择完整验证库时固定版本并核对许可；自有子集实现遇到不支持的约束或外部引用时，在任何工具执行前报 schema_unsupported，不能忽略约束并宣称兼容完整 JSON Schema。空 Schema 的含义和 extra properties 策略要有测试。

description、title、default、examples 等说明性注解可以保留；default 不自动补参数，也不授予权限。区分注解与验证约束，不能因为允许注解就忽略不支持的验证关键字。

### 4.3 模型结果投影

必须另建显式 DTO/投影方法，例如 `PtcModelResultProjector`。允许送往模型的字段限定为：

~~~json
{
  "runId": "host-created-id",
  "status": "completed",
  "output": [{"count": 2, "ids": [1, 3]}],
  "metrics": {"callsAttempted": 4, "callsStarted": 4, "callsFinished": 4},
  "artifactIds": [],
  "traceRef": "host-created-reference-or-null",
  "failure": null,
  "isolation": "inProcess"
}
~~~

完整 ToolRecords、原始工具结果、宿主路径、调用参数、内部异常栈及业务对象不进入该 DTO。`traceRef` 由宿主创建，不能由脚本指定文件路径。

禁止直接序列化整个 PtcExecutionResult 作为工具回复。投影白名单必须有行为测试：增加一个巨大原始字段时，模型结果仍保持小而完整。总投影超过 MaxModelResultBytes 时返回可解析的 `model_output_limit_exceeded`，不按字符截断 JSON。

JSON 输入和输出均检查深度、重复属性、UTF-8 字节数与合法值。对循环对象、BigInt、非有限数等不能无损编码的值返回明确错误；不得默默转换或丢字段。超出 JavaScript 安全整数范围的业务数值由工具 Schema 明确使用字符串表达。

## 5. JavaScript 工具协议

模型提供函数体，运行时以固定的 async 函数包装执行；不要求模型自己写 IIFE。工具名与参数经过 JSON 编码传递，禁止拼接进可执行源码。

| 脚本接口 | 返回 | 约束 |
|---|---|---|
| `inputs` | 本次输入的普通 JSON 值 | V0 要求对象，独立复制 |
| `tools.list()` | 名称及短描述数组 | 只枚举本次授权集合，不返回全部 Schema |
| `tools.describe(name)` | 工具定义及 InputSchema | 查询已授权工具，不扩权 |
| `await tools.call(name, args)` | 标准工具结果对象 | args 必须是对象，经统一调用路径 |
| `text(value)` | 无业务返回 | 记录一项模型输出，立即校验总字节数 |

工具结果固定使用 camelCase：

~~~json
{
  "success": true,
  "message": "ok",
  "data": {"ids": [1, 2, 3]},
  "errorCode": null,
  "artifacts": []
}
~~~

`tools.call` 返回的是结果封装；示例必须通过 `result.data` 读取业务数据，不能假设所有工具直接返回数组。宿主业务失败和拒绝返回 `success:false` 与稳定 ErrorCode，脚本可据此选择下一步。运行预算、超时和取消是不可恢复的 Run 停止状态，脚本 catch 不能将其恢复为成功。

以下为用于独立消费者验收的完整脚本，工具由合成宿主实现：

~~~javascript
const listed = await tools.call("list_orders", {});
if (!listed.success) {
    text({ error: listed.errorCode });
    return;
}

const details = await Promise.all(
    listed.data.ids.map(id => tools.call("get_order", { id }))
);
const failed = details.filter(item => !item.success);
if (failed.length) {
    text({ failed: failed.map(item => item.errorCode) });
    return;
}

const overdueIds = details
    .map(item => item.data)
    .filter(order => order.overdue)
    .map(order => order.id);
text({ count: overdueIds.length, ids: overdueIds });
~~~

合成宿主的 list_orders 返回 `[1,2,3]`，get_order 仅将 1、3 标记为 overdue；预期输出 `{"count":2,"ids":[1,3]}`，实际工具调用四次。

`text` 是唯一的 V0 模型输出入口；函数 return 仅结束执行，不隐式把任意返回对象送入上下文。需要发现 Schema 时可以先 text 描述，再由下一轮程序执行实际业务。

## 6. 调度 预算 取消与失败

### 6.1 并发与顺序

- 每个引擎的 JavaScript 执行串行推进；并发仅发生在受控的宿主 I/O。
- 相邻 ParallelReadOnly 调用可在 MaxConcurrency 下并发。
- Serial 调用是顺序屏障：先等此前调用结束，再独占执行；后来的读不得越过它。
- Promise.all 的结果遵循输入顺序，不按完成先后重新排序。
- 默认队列有界，不能先创建几十万个 Task，再依靠 SemaphoreSlim 限制实际执行。
- CallsAttempted 原子增加，包含未授权、参数错误和排队后取消的尝试；循环发送失败调用同样消耗预算。
- Serial 默认只约束同一 Run。不同 Run 改写共享资源时，宿主必须提供共享锁；组件不能从工具名推断文件事务。
- V0 Athena 只开放只读工具；通用组件仍须用合成写工具测试读写屏障。

### 6.2 预算含义

第四节中的值是原型默认值，实施中修改要说明理由。所有预算必须有限且为正；零不表示无限。

| 预算类别 | 计量对象 | 达到上限后的行为 |
|---|---|---|
| 代码、输入、参数 | 进入解释器/宿主前的字符、字节与 JSON 深度 | 拒绝本次执行或调用 |
| 语句、递归、引擎内存 | 固定版本解释器能约束的部分 | 停止 Run，标注原因 |
| 总时间 | 从进入协调器开始，包括排队、审批和工具等待 | TimedOut，取消未开始调用 |
| 单工具时间 | 实际工具调用，包括宿主审批 | 取消该调用并停止 Run |
| 调用数与待处理数 | 已尝试调用及队列长度 | 停止接收新调用 |
| 原始结果和总数据 | 交给脚本及保留为完整记录的 JSON 数据 | 明确报告数据上限，不返回被截断的成功对象 |
| 输出及最终投影 | 显式输出和模型结果的 UTF-8 长度 | 保留已完成输出并报告限制 |
| 附件引用 | 唯一引用数量 | 拒绝新增引用，记录限制 |

总时间不因 await、重新进入引擎或新建一轮工具调用而重置。审批等待计入时限；未来如需暂停预算，必须设计明确状态，不能悄悄无限等待。

EngineMemoryBytes 是解释器约束，不是宿主进程总内存承诺。桥接、JSON、工具记录、队列和附件引用另行限制。只为实际放行的工具传递授权，不因大结果需要完整数据而关闭原有文件读取配额。

若工具已经执行，结果才超出上限，记录“调用已完成，结果未能交付脚本”，保留原始大小、调用状态和原因。不能把它标记成未执行，也不能为获得较小结果自动重跑可能有副作用的调用。

### 6.3 取消与在途操作

- 链接调用方取消、总时限及工具时限，传到每个宿主调用。
- 已取消或停止的 Run 不得再启动排队工具，JavaScript 捕获异常也不能解除停止标记。
- 第一项停止原因由宿主锁定，区分调用方取消、工具/总时限和预算触发；不能仅从异常文字推测。BudgetExceeded 等原因可以取消内部令牌，但不能被归成“用户停止”。
- 返回前等待在途调用完成，最多 DrainTimeout；不得让未 await 的 Promise 悄悄继续业务操作。
- 忽略 CancellationToken 的宿主无法被进程内解释器强制停止。超过排空时间后将该调用标为 OutcomeUnknown，宿主保留观察与清理责任；原型文档如实报告此限制。
- 在途操作回调不得在 Run 已终止后写入复用引擎或属于其他 Run 的记录。
- 不得通过 .Result、.Wait 或 GetAwaiter().GetResult 桥接异步 I/O。

### 6.4 错误与事件

稳定错误码至少覆盖：`tool_not_available`、`tool_denied`、`invalid_arguments`、`schema_unsupported`、`tool_failed`、`invalid_code`、`script_error`、`call_limit_exceeded`、`data_limit_exceeded`、`output_limit_exceeded`、`model_output_limit_exceeded`、`deadline_exceeded`、`cancelled`、`observer_error`、`host_error`。

同一 Run 的事件使用单调递增 Sequence。至少有 RunStarted、CallQueued、CallStarted、CallFinished、RunFinished；每个调用 ID 唯一，Started 的调用有且只有一个终态，未开始的拒绝也有记录。事件 sink 使用有界交付；若异常影响交付，停止新调用并报告 observer_error，不能空 catch 后假装完整。

Run 结束时的元数据收尾使用独立且有短时限的清理令牌。业务令牌取消后仍要结束卡片和记录终态；清理不能再启动业务工具，也不能用无限等待延长停止。

V0 不自动重试工具或整段脚本。模型请求失败后的 SDK 重试属于宿主行为，与工具执行重试分开。执行记录用于审计，不能据此声称“可安全重放”或“恰好一次”。

## 7. Athena 当前入口与适配设计

### 7.1 已核对的代码入口

| 入口 | 当前行为 | 实施关注点 |
|---|---|---|
| [IFunctionRegistry](../Services/Interfaces/IFunctionRegistry.cs) | 提供工具定义、ExecuteAsync 及 FunctionResult | 仅在 Athena 适配器转换类型 |
| [FunctionRegistry](../Services/Functions/FunctionRegistry.cs) | JSON/Schema 校验、审批、实际执行、上下文结果预算 | 所有子调用继续进入 ExecuteAsync |
| [OpenAIChatService](../Services/OpenAIChatService.cs) | 获取工具快照，执行工具，回填结果 | 在真实调用点捕获 PTC 宿主上下文 |
| [EffectiveRequestRuntimeSnapshot](../Services/Context/EffectiveRequestRuntimeSnapshot.cs) | 一个用户回合固定 ToolDefinitions、ToolFingerprint | 从这个快照构造授权，不使用全局工具全集 |
| [ToolApprovalContext](../Services/ToolApprovalContext.cs) | Interactive、NonInteractive、Trusted 与委托任务 | 每次实际调用重新进入正确作用域 |
| [ToolExecutionContext](../Services/SubAgents/ToolExecutionContext.cs) | AsyncLocal 传递取消令牌 | 传入链接后的工具取消令牌 |
| [IConversationSessionAccessor](../Services/Interfaces/IConversationSessionAccessor.cs) | 会话与工作区作用域 | 不同 Run 不能串用 |
| [ToolCallParallelism](../Services/ToolCallParallelism.cs) | 只读并发及审批限制 | 共用风险判定与 SensitiveLocations |
| [McpDiscoveryFunctions](../Services/Mcp/McpDiscoveryFunctions.cs) | 发现、Schema、调用 meta-tool | 其调用路径目前向 MCP 传 default 令牌 |
| [ConversationPersistenceHelper](../Models/ConversationPersistenceHelper.cs) | 消息持久化白名单 | UI 子调用不能扩写共享消息 schema |

README 的“隔离 Tool Agent”等历史表述不能代替代码核对。本次只按上表描述适配入口，后续修改相关行为时同步 README、AGENTS.md 和 CLAUDE.md。

### 7.2 新增的宿主适配职责

建议新增以下类型，文件名为实施建议：

- `Services/Ptc/AthenaPtcToolHost.cs`：工具快照与结果转换，实际调用 Registry。
- `Services/Ptc/AthenaPtcInvocationScope.cs`：宿主创建的本次调用权限及上下文；不交给脚本。
- `Services/Ptc/AthenaPtcEventAdapter.cs`：事件投影到现有过程卡片。
- `Services/Ptc/AthenaPtcRunStore.cs`：保存可查询的执行记录，返回不含路径的引用。
- `Services/Functions/PtcFunctions.cs`：实现 execute_program 的应用工具入口。

构造依赖可能形成 Registry → PtcFunctions → ToolHost → Registry 环。采用运行时工厂惰性解析 Registry，或提取共享执行分派器；不得用生产路径上的 nullable 依赖加静默空分支解决。可参考现有 SubAgentOrchestrator 的惰性解析模式。

通用包只定义 IPtcToolHost；以上类型留在 Athena，不能反向引用进包。

除新增类型外，按以下位置接入，实施时核对现有构造参数和测试链接方式：

| 现有文件 | 适配修改 |
|---|---|
| `App.axaml.cs` | 注册协调器、运行时、适配工厂与记录服务；生产必需依赖不静默置空 |
| `Models/AppConfig.cs` | 增加缺省关闭的 PtcEnabled，保持用户配置序列化兼容 |
| `Services/Functions/FunctionRegistry.cs` | 注册工具 Schema，按开关披露，注入 PtcFunctions |
| `Services/ToolRiskClassifier.cs` | 明确登记 execute_program 为 Sensitive |
| `Services/OpenAIChatService.cs` | 在实际外层调用点绑定上下文与工具快照，保留现有回填和中断修复 |
| `Models/PromptTemplates.cs` 或现有工具描述入口 | 提供脚本协议与用法，只在功能启用时进入请求，回合内保持稳定 |
| 现有 ViewModel/Controls 展示边界 | 消费通用 DTO 事件并调度 UI；服务接口不引用 Avalonia 或 ViewModel 类型 |
| 现有测试工程与必要桩件 | 增加真实主循环断言；仅按实际需要更新 Compile Include 和 AppConfig 桩件 |

### 7.3 开关与工具暴露

新增用户控制的 `PtcEnabled`，缺省 false。未启用时不下发 execute_program；即使模型猜中名称，PtcFunctions 仍明确拒绝执行。开关不加入模型可写配置目录。

初始工具集取以下交集：

~~~text
runtime.ToolDefinitions 的名称快照
∩ 宿主允许 PTC 使用的工具名单
∩ 当前执行模式允许的工具
~~~

Athena V0 只开放：`read_system_file`、`get_file_info`、`search_in_file`、`search_in_directory`、`list_system_directory`。默认保留现有直接工具调用，PTC 作为可选入口，便于固定模型和任务作对照。

V0 不授予 terminal、配置修改、cron、子 Agent、浏览器、MCP 调用或 execute_program 自身。需要扩展时逐项补权限、取消和结果形状测试。子 Agent 与 KB maintenance 暂不获得 PTC 授权，不能被 Interactive 主对话的权限隐式放开。

execute_program 的模型参数仅有 code 和可选 inputs。AllowedTools、Limits、ConversationId、工作区、审批模式、父调用 ID 均由宿主提供。工具 Schema 关闭额外顶层属性，禁止模型自报权限。

### 7.4 审批与上下文传播

在 OpenAIChatService 的 RunOneAsync、紧邻外层 Registry 调用的位置创建 PTC 宿主作用域，绑定父 tool_call_id、runtime 工具快照、ConversationId、WorkspaceId、委托任务与执行模式。沿用现有快照指纹机制，不在回合中途修改 API 工具列表。

AthenaPtcToolHost 的每次 InvokeAsync 应执行：

1. 检查本次授权、递归限制和取消。
2. Enter 会话、工作区和链接取消令牌作用域。
3. 根据捕获的宿主模式进入审批作用域，Interactive 携带同一委托任务。
4. 紧邻 await 调用 FunctionRegistry.ExecuteAsync。
5. 处理结果并再次检查取消状态。

不假定 AsyncLocal 一定穿过解释器调度、异步迭代器或未来 Worker；实际执行点显式恢复作用域。不能为了使子调用成功而使用 EnterTrusted 或关闭审批。

execute_program 在 Athena V0 明确归 Sensitive，按现有规则审批外层程序；子调用仍经过 Registry。不要根据代码字符串中“看起来只读”就把整个工具永久标为 ReadOnly。PtcEnabled 为 true 也不是对所有程序的永久批准。

GetScheduling 复用 ToolCallParallelism.IsParallelSafe，使用与审批相同的配置来源和 SensitiveLocations；Strict、Automatic 或无法保证免弹窗时串行。若实际策略在排队中发生变化，保守降级，不并发开启多个审批窗口。

### 7.5 完整工具数据与上下文预算

当前 FunctionResult.ToJson 会优先返回 BudgetedJson；SerializeRaw 则序列化原始的 success/message/data。PTC 需要计算用的完整数据与模型阅读用的投影分别处理：

- 子调用仍经 ExecuteAsync 获得 FunctionResult，不绕过 Schema、审批或文件安全。
- 在适配器内，从原始业务 Data/SerializeRaw 构造受 PTC 单结果及总数据额度约束的 JSON 快照。
- 不把已有 BudgetedJson 当成完整业务数据进行计数或筛选，也不能关闭全局 MaxToolResultChars。
- 工具本身已有分页、文件块和搜索结果上限仍生效。PTC 的“完整”只指该次工具实际返回的结果，不代表整个文件或所有搜索匹配。
- 外层 PtcFunctions 只将模型投影封装为 FunctionResult；外层原有上下文预算继续生效。
- 从实际 MaxToolResultChars 预留外层封装开销，导出适合的模型输出额度。若最终预算仍不能容纳有效 JSON，返回短且可解析的限制错误，不能依赖通用截断器破坏 PTC 输出结构。

必须测试“结果超过普通模型预算，但在 PTC 数据额度内”的场景：脚本看到完整的该次结果，模型只看到准确聚合值。

### 7.6 UI 存档与附件

- 对模型协议，只有外层 execute_program 的 assistant 调用与对应 tool 结果；子调用走事件通道，不伪造成供应商 tool 消息，避免悬空 tool_call_id。
- UI 用 ParentToolCallId/RunId 分组显示真实子调用及终态。取消和异常也必须结束卡片。
- 完整记录由 AthenaPtcRunStore 放在宿主私有数据目录，路径只能由服务生成；每 Run 有大小上限，整体容量和失败处理明确。
- 完成记录以原子方式写入，再把 traceRef 置入外层结果。保存失败要报告，不能返回不存在的可查询引用。
- 取消时保存终态采用有界清理令牌，不能因为业务令牌已经取消而永远留下 running 记录。
- V0 不自动回收被会话引用的记录；存储满时拒绝新执行并给出处理入口，不能先删历史再继续。
- 外层结果引用随现有消息 Content 持久化，完整记录保持不可变；分支可引用同一份历史记录。测试重开、rewind/fork 后的可读性，回滚历史不能再次执行程序。
- 不为 UI 状态向 ChatMessage、ToolCallEntry 等共享持久化类型添加未定义字段。确需扩展 schema 时同步 Clone/恢复逻辑及白名单测试。
- GeneratedAttachments 转换为宿主附件引用并累积到外层 FunctionResult.GeneratedAttachments；即使后续失败，已经完成的附件仍应准确显示，不重复生成。
- 通用组件不读取附件路径，也不把附件二进制塞进模型投影。宿主对路径、大小、所有权和去重负责。

### 7.7 MCP 扩展门槛

MCP 不列入 Athena V0 授权。当前 CallToolAsync 向 _host.CallToolAsync 传 default 令牌；未修复和验证前，不得声称 PTC 停止能终止 MCP 调用。

未来接入时，必须按真实 MCP 工具身份授权，固定 Schema 快照，保留当前敏感审批，并传播 CancellationToken。不能只授权一个 mcp_call_tool meta-tool 就开放其背后的全部服务器能力。当前 Content 可能是文本 JSON，只有确实符合声明的结构化协议才解析为数据，不把任意文本冒充带类型结果。

## 8. NuGet 元数据与真实消费验证

### 8.1 打包要求

- 明确 PackageId、Version、Authors、Description、PackageReadmeFile、PackageLicenseExpression、RepositoryUrl 和文档输出。
- 原型版本建议 `0.1.0-preview.1`；两包版本一致，JavaScript 包声明对 Core 的正确版本依赖。
- RepositoryUrl 使用真实项目地址；独立发布位置未确定时记录待办，不填写虚构地址。
- 包内包含 XML API 文档、README 和必要第三方声明；不包含测试、obj、宿主业务程序集、用户配置、凭据或运行记录。
- 生成并提交必要的依赖锁文件；构建验证使用 locked-mode。
- 对外文档明确执行模式、预算、取消边界、错误协议、宿主责任和实际测试平台。
- V0 不声明尚未测试的 NativeAOT、裁剪安全或多目标框架支持。

### 8.2 构建与打包命令

以下从仓库根目录执行，前提是 M0 已创建相应工程；不是当前仓库已有命令。首次还原生成并审查锁文件后再使用 locked-mode：

~~~bash
dotnet restore Libraries/Ptc/Ptc.slnx --use-lock-file
dotnet restore Libraries/Ptc/Ptc.slnx --locked-mode
dotnet test Libraries/Ptc/Ptc.slnx -c Release --no-restore --logger trx --results-directory artifacts/ptc/test-results
dotnet pack Libraries/Ptc/src/Ptc.Core/Ptc.Core.csproj -c Release --no-restore -p:Version=0.1.0-preview.1 -o artifacts/ptc/feed
dotnet pack Libraries/Ptc/src/Ptc.JavaScript/Ptc.JavaScript.csproj -c Release --no-restore -p:Version=0.1.0-preview.1 -o artifacts/ptc/feed
~~~

不能仅凭 dotnet test 退出 0 判定执行过测试：记录发现和执行的测试数量，并确认注入一个失败断言会导致非零退出。打包脚本检查 nupkg/nuspec 中的 ID、版本、依赖、目标框架及程序集，不混入同名旧包。

### 8.3 包消费者必须从干净缓存安装

verify-package-consumer 脚本必须完成以下流程，不能只运行源码示例：

1. 创建仓库外临时目录，复制只有 PackageReference 的独立消费者源码。
2. 创建本次专用的空全局包目录；不使用开发机已有包缓存证明安装成功。
3. 生成独立 NuGet.Config，clear 默认源，添加本地 feed 与 nuget.org。用 packageSourceMapping 把本组件的 ID 前缀固定到本地源，第三方依赖走公共源。
4. 两个组件依赖固定为本次原型版本，消费者无 ProjectReference、源码 Compile Include 或指向开发输出的 DLL Reference。
5. 用如下形式还原并运行；路径由脚本创建，不把临时目录写入仓库配置。
6. 检查 project.assets.json、实际加载程序集路径、包目录和依赖图，确认加载的是本次包。
7. 运行第五节的脚本及拒绝/取消用例，记录输出、退出码与执行统计。
8. 再运行一次，确认没有通过临时源码路径、残留 DLL 或旁路程序集掩盖缺失包资产。

~~~bash
dotnet restore "$ptc_consumer_project" --configfile "$ptc_feed_config" --packages "$ptc_fresh_packages" --no-http-cache --force --use-lock-file
dotnet run --project "$ptc_consumer_project" -c Release --no-restore
~~~

上面的 ptc_* 变量由验证脚本赋值。NuGet.Config 中本地 feed 使用正确转义的绝对路径。组件 ID 前缀确定后同步 packageSourceMapping，禁止因同名公共包存在而意外从公共源安装。

### 8.4 Athena 的包引用验证

Athena 也用本次生成的包做验证。可采用本地独立 NuGet.Config 与命令行 --configfile；不修改用户级 NuGet 源，不把机器特定绝对路径或任何密钥提交到项目。

若为快速开发临时使用 ProjectReference，M5 前必须切回 PackageReference，并从干净组件缓存重新还原。组件包的依赖图不得出现 Athena；Athena 可以依赖组件，方向保持单向。

## 9. 测试与验收矩阵

所有新增不变量配有会失败的断言。表中用例为实施要求，均尚待后续实现；第一节的临时验证不能替代这些测试。

### 9.1 组件测试

| ID | 场景 | 必须验证的结果 |
|---|---|---|
| C01 | 依赖链、循环、条件和本地聚合 | 中间结果供下一工具使用，最终聚合准确 |
| C02 | 有界并发、倒序完成 | 在途数不超限，Promise.all 保持输入顺序 |
| C03 | 读、写、读及同批混合 | Serial 屏障不被后来的读取越过 |
| C04 | 不可用名字、大小写变体、特殊字符名 | 无越权、无名称碰撞，名称不作为源码执行 |
| C05 | 额外参数、错误类型、重复属性、深 JSON | 执行前拒绝，实际工具未调用 |
| C06 | 未授权调用循环、并发抢占额度 | 尝试数精确，拒绝也消耗额度，无超额启动 |
| C07 | 无限循环、递归、异步恢复后继续循环 | 总预算生效，捕获异常不能解除停止 |
| C08 | 单结果、总数据、输出及投影超限 | 明确错误；JSON 完整；已执行与未执行可区分 |
| C09 | 主动取消、排队取消、工具超时 | 不启动新调用，传递令牌，正确终态 |
| C10 | 未 await Promise、忽略取消的宿主 | 排空有界，OutcomeUnknown 如实报告 |
| C11 | 两个并行 Run、调用结束后的迟到回调 | 引擎、上下文、输出及记录不串用 |
| C12 | JsonDocument 提前释放、对象可变共享 | 快照仍有效，无宿主引用泄漏 |
| C13 | 隐藏全局、反射入口、对象原型与异常信息 | 无意外 CLR 能力或秘密原始异常栈输出 |
| C14 | 宿主失败、脚本错误、拒绝与重复执行请求 | 错误可区分，不自动重跑已完成工具 |
| C15 | 执行事件及 observer 故障 | 序号、父子关系、终态准确，故障可见 |
| C16 | 大原始结果与小模型投影 | 原始记录完整，模型投影不包含原始大结果 |
| C17 | 附件成功后脚本失败 | 保留已生成引用，不重复或丢失附件 |
| C18 | 真实包消费者 | 没有源码/项目引用仍可运行，依赖来自指定 feed |

安全相关测试只覆盖已列举路径；记录通过不能改写成完成安全审计。并发测试用 Barrier、TaskCompletionSource 等同步条件证明重叠，不以“耗时小于某个毫秒数”作为唯一断言。

### 9.2 Athena 适配测试

| ID | 场景 | 必须验证的结果 |
|---|---|---|
| A01 | PtcEnabled 关闭、名称被模型猜中 | 声明不可见，直接猜名也拒绝 |
| A02 | 真实 OpenAIChatService 工具循环 | 声明包含 execute_program，实际子调用经生产 Registry |
| A03 | 本次快照授权及禁用工具 | 无法通过全局 Registry 调用未授权工具 |
| A04 | 审批 Off/Balanced/Strict/Automatic | 外层与子调用按现有策略执行，拒绝来源准确 |
| A05 | 会话、工作区、委托任务及取消令牌 | 每个实际子调用观察到正确作用域 |
| A06 | 两个会话同时运行 | 审批上下文与事件归属不串扰 |
| A07 | 普通上下文预算小、PTC 原始数据额度足够 | 脚本聚合准确，模型只收到有限结果 |
| A08 | Chat Completions 与 Responses 投影 | 只出现配对的外层 tool call/result，无子调用悬空消息 |
| A09 | 用户停止、工具异常、结果超额 | 过程卡片结束，供应商消息配对有效 |
| A10 | 重开存档、rewind/fork | 输出和执行引用可读，不重新运行工具 |
| A11 | 附件与文件安全 | 原有路径、软链接和配额规则仍生效 |
| A12 | 子代理及维护路径猜名调用 | V0 不被 Interactive 权限或 Trusted 模式放开 |
| A13 | 关闭开关后直接工具调用 | 原有调用与结果预算继续通过 |
| A14 | 默认包消费 | Athena 使用 nupkg，不依靠临时 ProjectReference |

A02/A08 使用脚本化 loopback HTTP 模型和生产 SDK/主循环，检查真实发送载荷；只 mock IPtcExecutor 或只测 PtcFunctions 不能完成这两项。默认测试不调用生产模型、不使用用户真实文件或凭据。

Athena 修改后的基础检查：

~~~bash
dotnet build Athena.UI.csproj -p:UseAppHost=false --nologo
dotnet run --project Athena.Archive.Tests/Athena.Archive.Tests.csproj -p:UseAppHost=false
bash Scripts/run-headless-tests.sh
~~~

Headless 必须走现有脚本；Windows 使用对应 ps1。不要对 Athena.UI.HeadlessTests 直接 dotnet run。Archive.Tests 是控制台断言程序，按实际入口运行；实施 Agent 先核对其当前命令与退出码。

### 9.3 运行时与平台声明

至少在当前 Linux 环境完成原型验收。配置 Linux、Windows、macOS 的 .NET 10 CI；实际通过的平台才能列为已验证。报告操作系统、架构、SDK、运行时、Jint、包 ID/版本及测试数量。没有执行的平台标为未验证，不以纯托管依赖推定已通过。

真实模型对照放在组件及适配测试通过之后，需要使用已授权的测试模型配置。固定任务、模型、推理配置和工具数据，比较完成正确率、模型请求数、实际 provider usage、耗时及工具次数。脚本源字符数、输出字节数和调用统计不能冒充 token 用量，也不引入本地 token 估算器。

## 10. 分阶段实施与完成定义

| 阶段 | 交付物 | 离开本阶段的条件 |
|---|---|---|
| M0 边界与骨架 | 编译排除、独立 solution、包 ID/TFM、依赖与许可 | Athena 无误编译；通用工程不引用 Athena；状态与分支已记录 |
| M1 Core | 契约、快照、授权、调度、额度、事件、投影器 | 核心失败路径及模型投影白名单测试通过 |
| M2 JavaScript | 固定版 Jint、JSON/Promise 桥接、引擎约束 | C01 至 C17 对应测试通过，并保留限制说明 |
| M3 打包与独立消费者 | 两个包、元数据、干净缓存消费脚本 | C18 通过；读取并核验包资产和依赖 |
| M4 Athena 适配 | 默认关闭的工具入口、权限、上下文、事件与记录 | A01 至 A07、A11、A12 通过 |
| M5 端到端回归 | 双协议 loopback、存档/停止/UI/附件与包引用 | A08 至 A10、A13、A14 及现有相关套件通过 |
| M6 交付证据 | 实施说明、测试报告、包及校验和、限制与使用示例 | 下方清单完成，未验证项明确列出 |

M0/M1 可以在不修改模型主循环的情况下完成。M4 开始前须已有能安装运行的包。每阶段按业务边界提交，避免把组件、打包和 Athena UI 改动混成无法独立回退的一批。

后续实施交付时必须附带：

- [ ] 公共 API 与脚本协议、错误和取消语义均与实现一致。
- [ ] 两个带版本的 nupkg，以及 SHA-256。
- [ ] 独立消费者从空组件缓存安装本地包的成功日志。
- [ ] 包内部文件、nuspec、TFM 与依赖图检查结果。
- [ ] 组件测试数量、退出码与关键失败断言证据。
- [ ] Athena 真实主循环与双协议 loopback 结果。
- [ ] 逐项审批、快照授权、作用域、停止、附件和存档测试结果。
- [ ] 当前已验证平台与明确未验证的平台。
- [ ] 原型限制、数据记录位置、额度耗尽和回退方式。
- [ ] 未公开发布 NuGet，除非用户另有明确发布授权。

功能回退优先关闭 PtcEnabled 并恢复直接工具调用。历史执行记录保持只读；关闭功能或回滚代码不能重放程序、删除已有附件或重建历史结果。

## 11. 实施期间的决策规则

1. 发现本文与当前代码冲突，先定位实际调用链并记录差异，再修改实现或设计；不能直接按 README 的旧架构改代码。
2. 以减少宿主耦合为标准，不为目录数量拆出空包，不为了“通用”把模型循环、UI、凭据或业务工具搬进 Core。
3. 执行预算、白名单、取消和投影属于代码不变量，必须有失败测试。提示词用于指导模型，不能担任权限控制。
4. Jint 接口或所选依赖在固定版本不可用时，先做最小重现并更新依赖选择；不以未发布 main 分支功能当成已安装包的能力。
5. 生成包失败、包消费者失败或 Athena loopback 失败时分别定位；不能以某一层成功替代其他层证据。
6. 对未完成阶段标明未完成；SDK 能运行、脚本能执行或 HTTP 200 都不等于组件已经集成。
7. 提交代码时同步根目录文档入口和实际受影响的功能说明；不顺带修复无关 UI、音频或历史文档问题。

## 12. 参考资料

以下资料用于实现核对；本文的 API、默认预算与阶段划分是本项目设计，不是这些项目的现成接口或性能承诺。

- [Jint 官方项目](https://github.com/sebastienros/jint)：解释器、许可与版本分支说明。实施以锁定的 NuGet 版本及其测试为准。
- [Jint 4.x 文档](https://github.com/sebastienros/jint/blob/4.x/README.md)：异步执行、TaskInterop、约束与线程安全；分支文档可能领先于已发布包。
- [Cloudflare Code Mode](https://github.com/cloudflare/agents/blob/main/packages/codemode/README.md)：工具协议、执行器边界和结果处理参考。
- [Pydantic AI CodeMode](https://pydantic.dev/docs/ai/harness/code-mode/)：代码调用宿主工具及受限运行时参考。
- [Microsoft CodeAct](https://learn.microsoft.com/en-us/agent-framework/agents/code-act)：宿主工具桥接、逐函数中间件与审批边界参考。
- [Microsoft.Extensions.AI](https://learn.microsoft.com/en-us/dotnet/ai/microsoft-extensions-ai)：未来可选的 .NET 生态适配方向。
- [Factory 的 harness 文章](https://factory.com/news/frontier-harness)：PTC、中间结果与执行循环的背景。
