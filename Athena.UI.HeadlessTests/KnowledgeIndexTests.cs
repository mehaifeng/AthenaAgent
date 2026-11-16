using Athena.UI.Models;
using Athena.UI.Services;
using Athena.UI.Services.Interfaces;
using Microsoft.Data.Sqlite;
using Serilog;

internal static class KnowledgeIndexTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    internal static async Task RunAsync()
    {
        var root = Directory.CreateTempSubdirectory("athena-knowledge-index-").FullName;
        var paths = new TemporaryPathService(root);
        var knowledge = paths.GetKnowledgeBaseDirectory();
        const string originalPath = "quartz.md";
        const string heading = "Index fixture";
        const string query = "quartz";
        try
        {
            Directory.CreateDirectory(knowledge);
            await File.WriteAllTextAsync(Path.Combine(knowledge, "AthenaSoul.md"), "# Soul\n\nTest fixture");
            await File.WriteAllTextAsync(Path.Combine(knowledge, originalPath), $"# {heading}\n\n{query}");

            var embedding = new GatedEmbeddingService();
            using (var service = new KnowledgeBaseService(Log.Logger, embedding, paths))
            {
                var initialLoad = service.InitializeAsync();
                try
                {
                    await embedding.ProbeStarted.Task.WaitAsync(Timeout);
                    await service.RefreshVectorCacheAsync();
                }
                finally
                {
                    embedding.ReleaseProbe.TrySetResult();
                    await initialLoad.WaitAsync(Timeout);
                }

                var refreshed = await service.SearchAsync(query).WaitAsync(Timeout);
                Require(embedding.ProbeCount == 2, "刷新期间收到的索引失效必须由下一次检索处理一次。");
                Require(refreshed.Any(result => result.FilePath == originalPath
                    && result.HeadingPath == heading && result.RetrievalMode == "hybrid"),
                    "刷新后的混合检索应保留文件及其分块标题。");
                await service.SearchAsync(query).WaitAsync(Timeout);
                Require(embedding.ProbeCount == 2, "索引已刷新时，下一次检索应复用现有缓存。");
            }

            const string invalidatedPath = "renamed.md";
            // Reopening the same SQLite index exercises the persisted chunk projection without embeddings.
            using (var keywordService = new KnowledgeBaseService(Log.Logger, platformPathService: paths))
            {
                var persisted = await keywordService.SearchAsync(query);
                Require(persisted.Any(result => result.FilePath == originalPath
                    && result.HeadingPath == heading && result.RetrievalMode == "keyword"),
                    "重新打开 SQLite 后，关键词检索应保留分块和标题路径。");

                await keywordService.CreateFileAsync("created.md", "# Created\n\nmagnetite");
                Require((await keywordService.SearchAsync("magnetite")).Any(result => result.FilePath == "created.md"),
                    "创建知识文件返回时，内容应能立即检索。");

                File.Move(Path.Combine(knowledge, originalPath), Path.Combine(knowledge, invalidatedPath));
                await keywordService.RefreshVectorCacheAsync();
                var renamed = await keywordService.SearchAsync(query);
                Require(renamed.Any(result => result.FilePath == invalidatedPath && result.HeadingPath == heading)
                    && renamed.All(result => result.FilePath != originalPath),
                    "索引失效后的检索应显示新文件名并移除旧文件名。");
            }

            // Open a fresh watcher so the rename below is the only event that can trigger a refresh.
            using var watcherService = new KnowledgeBaseService(Log.Logger, platformPathService: paths);
            await watcherService.InitializeAsync();
            const string externalPath = "outside-rename.md";
            File.Move(Path.Combine(knowledge, invalidatedPath), Path.Combine(knowledge, externalPath));
            using var stop = new CancellationTokenSource(Timeout);
            while (true)
            {
                var indexed = await watcherService.SearchAsync(query).WaitAsync(stop.Token);
                if (indexed.Any(result => result.FilePath == externalPath && result.HeadingPath == heading)
                    && indexed.All(result => result.FilePath != invalidatedPath))
                    break;
                await Task.Delay(25, stop.Token);
            }
            await watcherService.InitializeAsync().WaitAsync(Timeout);

            Console.WriteLine("[PASS] knowledge index: refresh revisions, SQLite projections, immediate creation and watcher changes");
        }
        finally
        {
            using var connection = new SqliteConnection($"Data Source={paths.GetVectorStoreFilePath()}");
            SqliteConnection.ClearPool(connection);
            Directory.Delete(root, recursive: true);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class GatedEmbeddingService : IEmbeddingService
    {
        private int _probeCount;
        public int ProbeCount => Volatile.Read(ref _probeCount);
        public TaskCompletionSource ProbeStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseProbe { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsConfigured => true;
        public string ModelId => "headless-knowledge-index";

        public void UpdateConfig(AppConfig config) { }

        public async Task<float[]?> GenerateEmbeddingAsync(string text)
        {
            if (text == "test" && Interlocked.Increment(ref _probeCount) == 1)
            {
                ProbeStarted.TrySetResult();
                await ReleaseProbe.Task;
            }
            return [1f];
        }

        public Task<List<float[]?>> GenerateEmbeddingsAsync(IEnumerable<string> texts) =>
            Task.FromResult(texts.Select(_ => (float[]?)new[] { 1f }).ToList());

        public float CosineSimilarity(float[] a, float[] b) => 1f;

        public Task<(bool Success, string Message)> TestConnectionAsync() => Task.FromResult((true, "ok"));
    }
}
