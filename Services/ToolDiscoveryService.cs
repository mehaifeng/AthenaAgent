using Athena.UI.Services.Interfaces;
using Microsoft.Data.Sqlite;
using OpenAI.Chat;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services;

/// <summary>
/// 工具定义（包含增强的元数据用于向量检索）
/// </summary>
public class ToolDefinition
{
    /// <summary>
    /// 工具名称
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 简短描述
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// 使用场景描述（用于向量检索增强）
    /// </summary>
    public string[] UseCases { get; set; } = Array.Empty<string>();

    /// <summary>
    /// 分类标签
    /// </summary>
    public string[] Tags { get; set; } = Array.Empty<string>();

    /// <summary>
    /// 工具类别
    /// </summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>
    /// OpenAI tool schema
    /// </summary>
    public object Schema { get; set; } = new { };

    /// <summary>
    /// 获取用于向量化的文本
    /// </summary>
    public string GetEmbeddingText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"工具名称: {Name}");
        sb.AppendLine($"描述: {Description}");
        sb.AppendLine($"类别: {Category}");

        if (UseCases.Length > 0)
        {
            sb.AppendLine("使用场景:");
            foreach (var useCase in UseCases)
            {
                sb.AppendLine($"- {useCase}");
            }
        }

        if (Tags.Length > 0)
        {
            sb.AppendLine($"标签: {string.Join(", ", Tags)}");
        }

        return sb.ToString();
    }
}

/// <summary>
/// 工具发现服务接口
/// </summary>
public interface IToolDiscoveryService
{
    /// <summary>
    /// 初始化工具向量库
    /// </summary>
    Task InitializeAsync();

    /// <summary>
    /// 发现相关工具
    /// </summary>
    /// <param name="intent">用户意图</param>
    /// <param name="maxResults">最大返回数量</param>
    /// <param name="minSimilarity">最小相似度阈值</param>
    /// <returns>相关工具列表（可能为空）</returns>
    Task<List<ToolDefinition>> DiscoverToolsAsync(string intent, int maxResults = 5, double minSimilarity = 0.3);

    /// <summary>
    /// 获取元工具定义
    /// </summary>
    ChatTool GetMetaTool();

    /// <summary>
    /// 获取所有工具定义
    /// </summary>
    List<ToolDefinition> GetAllToolDefinitions();

    /// <summary>
    /// 根据名称获取工具
    /// </summary>
    ToolDefinition? GetToolByName(string name);

    /// <summary>
    /// 将工具定义转换为 ChatTool
    /// </summary>
    ChatTool ToChatTool(ToolDefinition tool);
}

/// <summary>
/// 工具发现服务实现
/// 使用向量语义检索发现相关工具
/// </summary>
public class ToolDiscoveryService : IToolDiscoveryService
{
    private readonly IEmbeddingService _embeddingService;
    private readonly IFunctionRegistry _functionRegistry;
    private readonly ILogger _logger;
    private readonly string _dbPath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly SemaphoreSlim _initLock = new(1, 1);

    private List<ToolDefinition> _allTools = new();
    private List<ToolVector> _toolVectors = new();
    private bool _initialized;

    /// <summary>
    /// 元工具定义
    /// </summary>
    private readonly ToolDefinition _metaTool = new()
    {
        Name = "discover_tools",
        Description = @"发现可用的工具。当你需要执行操作但不确定有哪些工具可用时，先调用此功能。
                        可用的工具类别：
                        - 知识库管理：记录、搜索、修改用户信息（生日、偏好、重要事件等）
                        - 提醒调度：安排提醒、跟进消息、定时任务
                        - 配置管理：调整应用设置（主题、字体、AI参数等）
                        调用此工具后，系统会返回与你的任务最相关的工具。",
        Category = "系统",
        UseCases = new[]
        {
            "不确定有哪些工具可用",
            "需要了解工具的功能",
            "寻找合适的工具完成任务"
        },
        Tags = new[] { "系统", "工具发现", "帮助" }
    };

    private class ToolVector
    {
        public string Name { get; set; } = string.Empty;
        public float[]? Embedding { get; set; }
    }

    public ToolDiscoveryService(IEmbeddingService embeddingService, IFunctionRegistry functionRegistry, ILogger logger)
    {
        _embeddingService = embeddingService;
        _functionRegistry = functionRegistry;
        _logger = logger.ForContext<ToolDiscoveryService>();

        _dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Athena",
            "KnowledgeBase",
            "tools.db"
        );

        // 确保目录存在
        var dir = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    public async Task InitializeAsync()
    {
        await _initLock.WaitAsync();
        try
        {
            if (_initialized) return;

            // 加载所有工具定义
            _allTools = GetAllToolDefinitions();

            // 初始化数据库
            await InitializeDatabaseAsync();

            // 加载或生成工具向量
            await LoadOrGenerateToolVectorsAsync();

            _initialized = true;
            _logger.Information("工具发现服务初始化完成，共 {Count} 个工具", _allTools.Count);
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task<List<ToolDefinition>> DiscoverToolsAsync(string intent, int maxResults = 5, double minSimilarity = 0.3)
    {
        var results = new List<ToolDefinition>();

        try
        {
            if (!_initialized)
            {
                await InitializeAsync();
            }

            // 如果 Embedding 服务不可用，返回空
            if (!_embeddingService.IsConfigured || _toolVectors.Count == 0)
            {
                _logger.Warning("Embedding 服务不可用或工具向量为空，返回空工具列表");
                return results;
            }

            // 生成意图向量
            var intentEmbedding = await _embeddingService.GenerateEmbeddingAsync(intent);
            if (intentEmbedding == null)
            {
                _logger.Warning("生成意图向量失败");
                return results;
            }

            // 计算相似度
            var scoredTools = _toolVectors
                .Where(t => t.Embedding != null)
                .Select(t => new
                {
                    Tool = t,
                    Similarity = _embeddingService.CosineSimilarity(intentEmbedding, t.Embedding!)
                })
                .Where(x => x.Similarity >= minSimilarity)
                .OrderByDescending(x => x.Similarity)
                .Take(maxResults)
                .ToList();

            // 映射回工具定义
            foreach (var item in scoredTools)
            {
                var toolDef = _allTools.FirstOrDefault(t => t.Name == item.Tool.Name);
                if (toolDef != null)
                {
                    results.Add(toolDef);
                    _logger.Debug("发现工具: {Name}, 相似度: {Score:F3}", toolDef.Name, item.Similarity);
                }
            }

            _logger.Information("工具发现完成: intent='{Intent}', 找到 {Count} 个工具", intent, results.Count);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "工具发现失败");
        }

        return results;
    }

    public ChatTool GetMetaTool()
    {
        return ChatTool.CreateFunctionTool(
            _metaTool.Name,
            _metaTool.Description,
            BinaryData.FromObjectAsJson(new
            {
                type = "object",
                properties = new
                {
                    intent = new
                    {
                        type = "string",
                        description = "描述你想要完成的任务或目标"
                    }
                },
                required = new[] { "intent" }
            })
        );
    }

    public List<ToolDefinition> GetAllToolDefinitions()
    {
        // 从 FunctionRegistry 获取完整的工具定义（包含参数 schema）
        var toolsFromRegistry = _functionRegistry.GetToolDefinitions()
            .OfType<OpenAI.Chat.ChatTool>()
            .ToDictionary(t => t.FunctionName, t => t);

        // 工具元数据（用于向量检索）
        var toolMetadata = new Dictionary<string, (string Category, string[] UseCases, string[] Tags)>
        {
            // 知识库管理
            ["create_knowledge_file"] = ("知识库管理",
                new[] { "创建新的用户信息文件", "记录用户的重要信息到新文件", "为用户建立新的档案", "创建新的记忆条目" },
                new[] { "知识库", "创建", "文件", "记录", "新建" }),
            ["update_knowledge_file_diff"] = ("知识库管理",
                new[] { "修改用户信息中的特定内容", "更新用户的偏好设置", "编辑已有记录的某个部分", "精确修改文件中的特定段落" },
                new[] { "知识库", "更新", "修改", "编辑", "SEARCH/REPLACE" }),
            ["update_knowledge_file"] = ("知识库管理",
                new[] { "追加新信息到文件末尾", "完全替换文件内容", "记录新的日志条目" },
                new[] { "知识库", "更新", "追加", "替换" }),
            ["search_knowledge_base"] = ("知识库管理",
                new[] { "查找用户的个人信息（生日、地址等）", "回忆用户之前的偏好或决定", "搜索与某个主题相关的记录", "检索用户的历史对话内容", "查找用户之前提到的爱好、工作等", "搜索用户的重要事件或纪念日" },
                new[] { "知识库", "搜索", "检索", "回忆", "查询", "记忆", "查找" }),
            ["read_knowledge_file"] = ("知识库管理",
                new[] { "查看特定文件的完整内容", "在修改前查看当前文件", "获取完整文件用于编辑" },
                new[] { "知识库", "读取", "查看", "文件" }),
            ["delete_knowledge_file"] = ("知识库管理",
                new[] { "删除不再需要的文件", "清理过时的记录" },
                new[] { "知识库", "删除", "移除" }),
            ["list_knowledge_files"] = ("知识库管理",
                new[] { "查看知识库结构", "查找特定文件", "了解有哪些记录可用" },
                new[] { "知识库", "列表", "文件" }),

            // 提醒调度
            ["schedule_proactive_message"] = ("提醒调度",
                new[] { "设置提醒事项", "安排定时跟进", "创建周期性提醒", "预约未来的通知", "设置生日提醒、会议提醒", "安排每日/每周的例行提醒" },
                new[] { "提醒", "调度", "定时", "预约", "跟进", "通知" }),
            ["cancel_scheduled_message"] = ("提醒调度",
                new[] { "取消不需要的提醒", "删除已安排的任务" },
                new[] { "提醒", "取消", "删除" }),
            ["list_scheduled_messages"] = ("提醒调度",
                new[] { "查看当前有哪些提醒", "检查已安排的任务", "用户询问提醒列表" },
                new[] { "提醒", "列表", "查看" }),

            // 配置管理
            ["modify_app_config"] = ("配置管理",
                new[] { "调整 AI 的回复风格（温度参数）", "修改主题或字体大小", "更改语言设置", "调整上下文记忆长度", "开启或关闭功能" },
                new[] { "配置", "设置", "调整", "修改" }),
            ["get_app_config"] = ("配置管理",
                new[] { "查看当前设置", "了解配置参数", "检查 AI 参数配置" },
                new[] { "配置", "获取", "查看" })
        };

        // 合并：从 FunctionRegistry 获取完整定义，添加元数据
        var result = new List<ToolDefinition>();

        foreach (var kvp in toolsFromRegistry)
        {
            var name = kvp.Key;
            var chatTool = kvp.Value;

            var (category, useCases, tags) = toolMetadata.TryGetValue(name, out var meta)
                ? meta
                : ("其他", Array.Empty<string>(), Array.Empty<string>());

            // 从 ChatTool 提取参数 schema
            object schema = new { };
            try
            {
                var parametersJson = chatTool.FunctionParameters?.ToString();
                if (!string.IsNullOrEmpty(parametersJson))
                {
                    schema = System.Text.Json.JsonSerializer.Deserialize<object>(parametersJson) ?? new { };
                }
            }
            catch { }

            result.Add(new ToolDefinition
            {
                Name = name,
                Description = chatTool.FunctionDescription ?? "",
                Category = category,
                UseCases = useCases,
                Tags = tags,
                Schema = schema
            });
        }

        return result;
    }

    public ToolDefinition? GetToolByName(string name)
    {
        return _allTools.FirstOrDefault(t => t.Name == name);
    }

    /// <summary>
    /// 将工具定义转换为 ChatTool
    /// </summary>
    public ChatTool ToChatTool(ToolDefinition tool)
    {
        return ChatTool.CreateFunctionTool(
            tool.Name,
            tool.Description,
            BinaryData.FromObjectAsJson(tool.Schema)
        );
    }

    private async Task InitializeDatabaseAsync()
    {
        await _lock.WaitAsync();
        try
        {
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            await connection.OpenAsync();

            var createTableSql = @"
                CREATE TABLE IF NOT EXISTS tool_vectors (
                    name TEXT PRIMARY KEY,
                    embedding BLOB NOT NULL,
                    embedding_text TEXT NOT NULL,
                    updated_at TEXT DEFAULT CURRENT_TIMESTAMP
                );
            ";

            using var command = new SqliteCommand(createTableSql, connection);
            await command.ExecuteNonQueryAsync();

            _logger.Debug("工具向量数据库初始化完成");
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task LoadOrGenerateToolVectorsAsync()
    {
        if (!_embeddingService.IsConfigured)
        {
            _logger.Warning("Embedding 服务未配置，跳过工具向量生成");
            return;
        }

        await _lock.WaitAsync();
        try
        {
            // 尝试从数据库加载
            var storedVectors = await LoadToolVectorsFromDbAsync();

            // 检查是否需要重新生成（工具数量变化）
            if (storedVectors.Count == _allTools.Count)
            {
                _toolVectors = storedVectors;
                _logger.Information("从数据库加载 {Count} 个工具向量", _toolVectors.Count);
                return;
            }

            // 重新生成所有向量
            _logger.Information("开始生成工具向量...");

            _toolVectors.Clear();
            var embeddingTexts = _allTools.Select(t => t.GetEmbeddingText()).ToList();
            var embeddings = await _embeddingService.GenerateEmbeddingsAsync(embeddingTexts);

            for (int i = 0; i < _allTools.Count && i < embeddings.Count; i++)
            {
                if (embeddings[i] != null)
                {
                    _toolVectors.Add(new ToolVector
                    {
                        Name = _allTools[i].Name,
                        Embedding = embeddings[i]
                    });
                }
            }

            // 保存到数据库
            await SaveToolVectorsToDbAsync();

            _logger.Information("工具向量生成完成，共 {Count} 个", _toolVectors.Count);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<List<ToolVector>> LoadToolVectorsFromDbAsync()
    {
        var vectors = new List<ToolVector>();

        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();

        var sql = "SELECT name, embedding FROM tool_vectors";
        using var command = new SqliteCommand(sql, connection);
        using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            var embeddingBlob = (byte[])reader[1];
            vectors.Add(new ToolVector
            {
                Name = reader.GetString(0),
                Embedding = BytesToFloats(embeddingBlob)
            });
        }

        return vectors;
    }

    private async Task SaveToolVectorsToDbAsync()
    {
        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();

        using var transaction = connection.BeginTransaction();

        try
        {
            // 清空旧数据
            using (var deleteCommand = new SqliteCommand("DELETE FROM tool_vectors", connection, transaction))
            {
                await deleteCommand.ExecuteNonQueryAsync();
            }

            // 插入新数据
            foreach (var toolVector in _toolVectors)
            {
                var tool = _allTools.FirstOrDefault(t => t.Name == toolVector.Name);
                var embeddingText = tool?.GetEmbeddingText() ?? "";

                using var insertCommand = new SqliteCommand(
                    @"INSERT INTO tool_vectors (name, embedding, embedding_text, updated_at)
                      VALUES (@name, @embedding, @embeddingText, @updatedAt)",
                    connection, transaction);

                insertCommand.Parameters.Add(new SqliteParameter("@name", toolVector.Name));
                insertCommand.Parameters.Add(new SqliteParameter("@embedding", FloatsToBytes(toolVector.Embedding!)));
                insertCommand.Parameters.Add(new SqliteParameter("@embeddingText", embeddingText));
                insertCommand.Parameters.Add(new SqliteParameter("@updatedAt", DateTime.UtcNow.ToString("O")));

                await insertCommand.ExecuteNonQueryAsync();
            }

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private static byte[] FloatsToBytes(float[] floats)
    {
        var bytes = new byte[floats.Length * sizeof(float)];
        Buffer.BlockCopy(floats, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static float[] BytesToFloats(byte[] bytes)
    {
        var floats = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
        return floats;
    }
}
