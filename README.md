[![中文](https://img.shields.io/badge/-中文-red?style=flat-square)](README_CN.md) · **English**

---

# Athena Assistant

Athena is a sophisticated, highly autonomous desktop AI assistant built with **.NET 10** and **Avalonia UI**. It serves as an intellectual partner with deep system integration, proactive capabilities, and a modern, polished interface.

## ✨ Key Features

- **🧠 Multi-Model Intelligence**: Utilizes a tiered architecture with specialized models for reasoning (GPT-4o), context management (GPT-4o-mini), and semantic search.
- **🛠️ Direct Tool Calling**: Seamlessly interacts with your local system via secure file operations, web search, terminal execution, and application configuration.
- **📚 Local Knowledge Base**: A vector-powered semantic memory stored locally in Markdown files, ensuring privacy and instant retrieval.
- **⏰ Proactive Engagement**: Features an integrated task scheduler for reminders, follow-ups, and automated system checks.
- **🌍 Modern Cross-Platform UI**: Built with Avalonia UI and the Semi Design aesthetic, supporting both light/dark modes and multi-lingual interfaces (English & Chinese).
- **🛡️ Security-First Design**: Implements strict data sandboxing, file system protection, and secure API management.

## 🚀 Getting Started

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

### Installation & Running

1. **Clone the repository**:
   ```bash
   git clone https://github.com/your-username/AthenaAgent.git
   cd AthenaAgent
   ```

2. **Restore dependencies**:
   ```bash
   dotnet restore
   ```

3. **Run the application**:
   ```bash
   dotnet run
   ```

## 🏗️ Project Structure

- `Services/`: Core business logic, including AI integration, file system management, and task scheduling.
- `ViewModels/`: MVVM ViewModels for application state and UI logic.
- `Views/`: Avalonia XAML definitions for the user interface.
- `Models/`: Data structures, configuration schemas, and prompt templates.
- `Assets/`: Icons, localized strings, and static knowledge base files.

## 🛠️ Tech Stack

- **Framework**: Avalonia UI 11.3
- **Runtime**: .NET 10
- **UI Themes**: Semi.Avalonia, Ursa.Themes.Semi
- **AI SDK**: OpenAI SDK
- **Database**: SQLite (for logging and vector storage)
- **Logging**: Serilog

## 📄 License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.
