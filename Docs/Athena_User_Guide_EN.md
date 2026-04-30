# Athena User Guide (English)

## 1. Product Overview
Athena is a desktop AI assistant built with .NET and Avalonia UI. It combines chat, tool execution, browser automation, task scheduling, and local knowledge base management in one application.

## 2. Environment Requirements
- Windows / Linux / macOS (desktop environment)
- .NET 10 SDK
- Valid model API credentials (for the providers you use)

## 3. Quick Start
1. Clone project:
```bash
git clone https://github.com/mehaifeng/AthenaAgent.git
cd AthenaAgent
```
2. Restore dependencies:
```bash
dotnet restore
```
3. Run:
```bash
dotnet run
```

## 4. First-Time Configuration
1. Open `Config` tab.
2. Set model provider, endpoint, and API key.
3. Configure default model mappings and optional browser/search settings.
4. Save configuration and restart if required.

## 5. Core Tabs and Usage
### Chat
- Main interaction area for user requests and agent responses.
- Supports tool-driven tasks such as file operations and command execution.

### Tasks
- Create scheduled or recurring tasks.
- Use for reminders, delayed execution, and routine checks.

### Browser
- Run browser tasks (navigate, click, type, wait, extract).
- For pages with multiple tabs, the agent should switch/inspect active tab before repeating open actions.

### Knowledge Base
- Import and manage local markdown knowledge files.
- Supports semantic retrieval for context-enhanced responses.

### Logs
- Filter by time range, level, and keyword.
- Export currently filtered logs from the UI.

### About
- View version/project information.
- `OpenGitHub` opens the official repository:
  `https://github.com/mehaifeng/AthenaAgent`

## 6. Typical Workflow
1. Configure model credentials.
2. Start in `Chat` and provide a concrete objective.
3. If web action is needed, trigger browser task and verify results in browser evidence/logs.
4. Store reusable materials in `Knowledge Base`.
5. Use `Logs` tab for diagnostics and export when needed.

## 7. Troubleshooting
- API call failures: verify endpoint, key, and network access.
- Browser task loops: check latest tab state and logs to confirm target page actually changed.
- No logs in result: confirm level mapping and time range filter.
- Performance drops: reduce context length and trim unnecessary historical messages.

## 8. Security Notes
- Keep API keys in local configuration only.
- Review tool permissions before enabling high-risk operations.
- Exported logs may contain sensitive traces; store and share carefully.
