# PTC 原型：使用、验证与限制

设计基线保留在 [PTC_NuGet_Implementation_Design_CN.md](PTC_NuGet_Implementation_Design_CN.md)；实际命令、退出码、测试数量和未验证项以 [实施记录](../Libraries/Ptc/IMPLEMENTATION.md) 为准。设计要求不等于实现证据。

## 构建与本地包

两个 `net10.0` NuGet 原型包：`AthenaAgent.Ptc.Core`、`AthenaAgent.Ptc.JavaScript`，版本均为 `0.1.0-preview.1`。JavaScript 包固定使用 Jint 4.17.0，锁文件固定其第三方依赖；两个组件均不依赖 Athena、Avalonia、模型 SDK 或宿主凭据。

从仓库根目录先生成本地 feed，再构建 Athena：

```bash
bash Libraries/Ptc/scripts/build-local-feed.sh
dotnet build Athena.UI.csproj -p:UseAppHost=false --nologo
```

Windows 没有 Bash 时可依次执行：

```powershell
dotnet restore Libraries/Ptc/Ptc.slnx --locked-mode
dotnet pack Libraries/Ptc/src/Ptc.Core/Ptc.Core.csproj -c Release --no-restore -o artifacts/ptc/feed
dotnet pack Libraries/Ptc/src/Ptc.JavaScript/Ptc.JavaScript.csproj -c Release --no-restore -o artifacts/ptc/feed
dotnet build Athena.UI.csproj -p:UseAppHost=false --nologo
```

仓库内 NuGet.Config 把本组件前缀限定到相对路径本地源，其他依赖使用 nuget.org。不修改用户级源，也不要求公开发布。原型本地 feed 按当前源码重建，不提交机器路径或二进制缓存；独立消费者每次在仓库外生成新的锁文件并用 locked-mode 再次确认。组件源码工程的第三方依赖锁文件提交到仓库，组件构建使用 locked-mode。

## 开关与脚本

在“设置 → 工具审批”打开 PTC 原型开关，默认关闭，模型无法修改该开关。关闭后仍保留原有直接工具调用。一个用户回合内工具声明固定，开关或权限被收回后，实际调用点仍会拒绝后续 PTC 执行。

`execute_program` 只接收 `code` 和可选对象 `inputs`；权限、预算、会话、工作区、委托任务和父调用 ID 均由宿主提供。`code` 是 async 函数体，不需要 IIFE：

```javascript
const r = await tools.call("list_system_directory", { path: inputs.path });
if (!r.success) {
  text({ error: r.errorCode });
  return;
}
text({ count: r.data.entries.length });
```

- `tools.list()`：本次授权名称与短描述。
- `tools.describe(name)`：本次授权工具的 Schema；不可用时为 null。
- `await tools.call(name, args)`：对象参数，返回 `{success,message,data,errorCode,artifacts}`；业务数据通过 `result.data` 读取。
- `text(value)`：唯一模型输出入口；`return` 只结束执行，不隐式输出。
- 支持变量、条件、循环、async/await、Promise.all、Promise.allSettled。必须等待每个调用；未完成的 Promise 不能变成后台任务。
- 非有限数、BigInt、循环对象、稀疏数组、访问器等不能无损 JSON 编码的值明确失败；超过 JavaScript 安全整数范围的业务值须使用字符串。

## Athena V0 权限

本次回合工具快照与以下名单取交集：read_system_file、get_file_info、search_in_file、search_in_directory、list_system_directory。工具自身分页、文件读取配额、路径/软链接保护全部保留。“完整数据”只指该次工具实际返回的完整结果，不表示无限读取文件。

不授权 terminal、MCP、配置、cron、浏览器、子代理或递归 execute_program。子代理和维护路径猜中名称也拒绝。外层 execute_program 为 Sensitive；其获批不代替子调用审批。Strict/Automatic 串行，Off/Balanced 仅对明确免弹窗的只读调用并发；等待期间策略变化会重新判断，并独立串行化子调用审批。

## 预算、停止与记录

核心默认：代码 32,000 字符；100,000 语句；递归 64；引擎分配 32 MiB；总时间 30 秒；工具连同审批 10 秒；64 次尝试；最多 4 个并发、64 个待处理；JSON 深度 32；输入 128 KiB；参数 64 KiB；单结果 512 KiB；总数据 8 MiB；显式输出 16 KiB；最终投影 24 KiB；32 个唯一附件引用。工具目录最多 128 项、512 KiB。Athena 还会遵守用户的并发限制，并从普通结果预算预留外层封装空间。

预算、取消、超时是不可恢复的停止状态，脚本 catch 不能改回成功。第一停止原因被锁定，不把内部预算取消伪称为用户停止。无自动重试、自动修复或重放工具。Serial 只约束同一 Run；跨 Run 的共享写资源锁由宿主负责。

取消后最多排空 2 秒；忽略 CancellationToken 的宿主无法被进程内解释器强制终止，超时仍在途的调用记为 OutcomeUnknown。某些现有文件服务操作没有底层可取消 I/O：作用域中仍有链接令牌，但不能据此承诺瞬时终止。迟到结果不能改写已返回的记录。已执行但结果超过预算，记录其真实终态、原始大小和未交付原因，不称为“未执行”。

UI 子调用显示在外层工具行的展开详情中，仅走元数据事件；不会伪造供应商 tool 消息，也不会新增消息持久化字段。模型只收到白名单投影、有限的显式输出及不含路径的 traceRef，原始结果留在记录中。

记录在宿主数据目录 `AthenaData/PtcRuns`，每 Run 16 MiB、整体 256 MiB，执行前预留容量，完成后以临时文件原子提交，失败不返回不存在的引用。关闭开关、重开历史和分支不会重放程序。原型不自动删除被历史引用的记录；容量满时拒绝新执行。处理入口是设置页提示的数据目录：先关闭 PTC，完整备份关联历史、附件及 PtcRuns，再由宿主维护者扩展容量或迁移整个数据目录。不要只删某条会话仍引用的记录。`AthenaPtcRunStore.ReadAsync(traceRef)` 是宿主查询入口，禁止调用者指定文件路径。

## 验证命令与证据边界

```bash
dotnet restore Libraries/Ptc/Ptc.slnx --locked-mode
dotnet test Libraries/Ptc/Ptc.slnx -c Release --no-restore --logger trx --results-directory artifacts/ptc/test-results
bash Libraries/Ptc/scripts/build-local-feed.sh
bash Libraries/Ptc/scripts/verify-package-consumer.sh
dotnet run --project Athena.Archive.Tests/Athena.Archive.Tests.csproj -p:UseAppHost=false
ATHENA_HEADLESS_ONLY=ptc bash Scripts/run-headless-tests.sh
bash Scripts/run-headless-tests.sh
```

Windows 无头测试使用对应 ps1，不直接 dotnet run 无头工程。真实包消费者校验需开发环境 Python 3；这不是包的运行时依赖。独立源码示例 StandaloneHost 不算包消费证据。

安全声明仅为已测路径：Jint **进程内执行，不是强沙箱、不是安全审计**，不提供进程/操作系统级文件、网络、凭据隔离。Linux、Windows、macOS CI 已配置，只有实际执行成功的平台才算已验证；未做 NativeAOT、裁剪、多目标框架或跨平台认证。真实模型完成率、provider usage 和性能对照仍须另行授权测试，不能从解释器或脚本化 loopback 成功推断。没有公开发布 NuGet、推送或部署。
