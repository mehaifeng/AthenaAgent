[![English](https://img.shields.io/badge/-English-blue?style=flat-square)](README.md) · **中文**

---

# Athena 智能助手

Athena 是一款高度自主的桌面 AI 助手，基于 **.NET 10** 和 **Avalonia UI** 构建。它不仅是你的智能伙伴，更具备深度系统集成能力、主动服务机制和现代化的用户界面。

## ✨ 核心特性

- **🧠 多模型分层架构**：采用专用模型分工——推理（GPT-4o）、上下文管理（GPT-4o-mini）、语义搜索。
- **🛠️ 直接工具调用**：通过安全的文件操作、网页搜索、终端执行和应用配置与本地系统无缝交互。
- **📚 本地知识库**：基于向量检索的语义记忆系统，以 Markdown 格式本地存储，隐私安全，检索即所得。
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

- **框架**：Avalonia UI 11.3
- **运行时**：.NET 10
- **UI 主题**：Semi.Avalonia、Ursa.Themes.Semi
- **AI SDK**：OpenAI SDK
- **数据库**：SQLite（日志和向量存储）
- **日志**：Serilog

## 📄 开源协议

本项目基于 MIT 协议开源，详见 [LICENSE](LICENSE) 文件。
