[![English](https://img.shields.io/badge/-English-blue?style=flat-square)](README.md) · **中文**

---

# Athena 智能助手

Athena 是一款高度自主的桌面 AI 助手，基于 **.NET 10** 和 **Avalonia UI** 构建。它不仅是你的智能伙伴，更具备深度系统集成能力、主动服务机制和现代化的用户界面。

## ✨ 核心特性

- **🧠 唯一供应商与模型分工**：唯一的 OpenAI SDK 兼容供应商统一保存连接信息；主对话、标题、上下文压缩、自动审批、Embedding、浏览器与子代理分别选择模型。TTS 与生图保留扩展页独立连接。
- **🛠️ 隔离 Tool Agent**：主对话只委托一个受控的执行 Agent，由它调用内置工具与 MCP 并返回证据；每项实际调用仍以可展开的分组卡片呈现。
- **🦉 并行子代理**：批量派发相互隔离的子代理并发处理任务，以动画化的"猫头鹰之城"总览呈现进度。
- **🌐 浏览器自动化**：在隔离浏览器会话中以视觉引导（Playwright + Set-of-Marks）观察网页、点击可见控件、填写简单表单、上传本地文件并提取页面证据。
- **📚 本地知识库**：基于向量检索的语义记忆系统，以 Markdown 格式本地存储，支持混合检索（向量 + BM25/FTS）与后台自维护，隐私安全，检索即所得。
- **📄 文档解析**：基于 MinerU 从附件文档中提取大纲与正文内容。
- **🎨 图像生成与语音**：支持跨轮次连贯的内联图像生成，以及 TTS 语音播放。
- **↩️ 会话归档、回滚与分支**：会话持久化且可浏览，任意轮次均可回滚或分支为新的分支会话。
- **⏰ 主动任务调度**：内置任务调度器，支持提醒、跟进和自动化系统检查。
- **🌍 现代跨平台界面**：基于 Avalonia UI 和 Semi Design，支持浅色/深色模式及多语言界面（英文 & 中文）。
- **🛡️ 安全优先设计**：严格的数据沙箱、文件系统保护和 API 安全管理。

## 🚀 快速开始

### 环境要求

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

### 安装与运行

1. **克隆仓库**：
   ```bash
   git clone https://github.com/your-username/AthenaAgent.git
   cd AthenaAgent
   ```

2. **还原依赖**：
   ```bash
   dotnet restore
   ```

3. **运行应用**：
   ```bash
   dotnet run
   ```

## 🏗️ 项目结构

- `Services/`：核心业务逻辑，包括 AI 集成、文件系统管理和任务调度。
- `ViewModels/`：MVVM ViewModel，负责应用状态和 UI 逻辑。
- `Views/`：Avalonia XAML 界面定义。
- `Models/`：数据结构、配置模型和提示词模板。
- `Assets/`：图标、本地化字符串和静态知识库文件。

## 🛠️ 技术栈

- **框架**：Avalonia UI 12.0
- **运行时**：.NET 10
- **UI 主题**：Semi.Avalonia、Irihi.Ursa.Themes.Semi
- **AI SDK**：OpenAI SDK 2.x（对话、嵌入、图像生成、Audio/TTS、工具调用；统一重试与超时策略）
- **Markdown**：LiveMarkdown.Avalonia（流式输出、内联图片）
- **浏览器自动化**：Microsoft.Playwright
- **音频**：LibVLCSharp
- **数据库**：SQLite（向量存储）
- **日志**：Serilog（文件 + 控制台）

## 📄 开源协议

本项目基于 MIT 协议开源，详见 [LICENSE](LICENSE) 文件。
