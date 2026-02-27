using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Markup.Xaml;
using Athena.UI.ViewModels;
using Athena.UI.Views;
using Athena.UI.Services;
using Athena.UI.Services.Interfaces;
using Athena.UI.Services.Functions;
using Athena.UI.Services.Platform;
using Athena.UI.Models;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace Athena.UI;

public partial class App : Application
{
    /// <summary>
    /// 服务提供者（用于依赖注入）
    /// </summary>
    public static IServiceProvider? Services { get; private set; }

    /// <summary>
    /// 平台路径服务
    /// </summary>
    private static IPlatformPathService? _platformPathService;

    public override void Initialize()
    {
        // 初始化平台路径服务（需要在日志之前初始化）
        _platformPathService = new DesktopPlatformPathService();

        // 初始化 Serilog
        var logDir = _platformPathService.GetLogDirectory();
        var dbPath = Path.Combine(logDir, "logs.db");

        Log.Logger = SerilogConfiguration.CreateLogger(dbPath);
        Log.Information("应用程序启动中... 平台: Desktop");

        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Log.Information("开始初始化框架...");

        // 配置依赖注入
        var services = new ServiceCollection();
        ConfigureServices(services);
        Services = services.BuildServiceProvider();

        // 桌面平台使用经典桌面生命周期
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Avoid duplicate validations from both Avalonia and the CommunityToolkit.
            // More info: https://docs.avaloniaui.net/docs/guides/development-guides/data-validation#manage-validationplugins
            DisableAvaloniaDataAnnotationValidation();

            // 从 DI 容器获取 ViewModel
            var mainViewModel = Services.GetRequiredService<MainWindowViewModel>();

            desktop.MainWindow = new MainWindow
            {
                DataContext = mainViewModel,
            };

            Log.Information("主窗口创建完成");
        }

        base.OnFrameworkInitializationCompleted();
        Log.Information("框架初始化完成");
    }

    /// <summary>
    /// 配置服务依赖注入
    /// </summary>
    private static void ConfigureServices(IServiceCollection services)
    {
        Log.Debug("配置依赖注入服务...");

        // 平台路径服务（单例）
        services.AddSingleton<IPlatformPathService>(_platformPathService!);

        // 日志服务（单例）
        services.AddSingleton<ILogService, LogService>();

        // 配置服务（单例）
        services.AddSingleton<IConfigService, ConfigService>();

        // 任务调度器（单例，UI 和 Function Calling 共享）
        services.AddSingleton<ITaskScheduler>(sp =>
        {
            var logger = Log.ForContext<Services.TaskScheduler>();
            var scheduler = new Services.TaskScheduler(logger);
            scheduler.Start(); // 启动调度器
            Log.Information("任务调度器已启动");
            return scheduler;
        });

        // Embedding 服务（单例，用于向量语义检索）
        services.AddSingleton<IEmbeddingService>(sp =>
        {
            var configService = sp.GetRequiredService<IConfigService>();
            var config = configService.Load();
            var logger = Log.ForContext<OpenAIEmbeddingService>();
            Log.Information("Embedding 服务初始化，模型: {Model}", config.EmbeddingModel);
            return new OpenAIEmbeddingService(config, logger);
        });

        // 知识库服务（单例）
        services.AddSingleton<IKnowledgeBaseService>(sp =>
        {
            var logger = Log.ForContext<KnowledgeBaseService>();
            var embeddingService = sp.GetService<IEmbeddingService>();
            var platformPathService = sp.GetRequiredService<IPlatformPathService>();
            var service = new KnowledgeBaseService(logger, embeddingService, platformPathService);

            // 异步初始化（加载向量缓存）
            _ = Task.Run(async () =>
            {
                try
                {
                    await service.InitializeAsync();
                    Log.Information("知识库服务初始化完成");
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "知识库服务初始化失败");
                }
            });

            return service;
        });

        // Function 相关类（使用工厂方法提供 Logger）
        services.AddSingleton<ProactiveMessagingFunctions>(sp =>
        {
            var taskScheduler = sp.GetRequiredService<ITaskScheduler>();
            var logger = Log.ForContext<ProactiveMessagingFunctions>();
            return new ProactiveMessagingFunctions(taskScheduler, logger);
        });

        services.AddSingleton<KnowledgeBaseFunctions>(sp =>
        {
            var knowledgeBase = sp.GetRequiredService<IKnowledgeBaseService>();
            var logger = Log.ForContext<KnowledgeBaseFunctions>();
            return new KnowledgeBaseFunctions(knowledgeBase, logger);
        });

        services.AddSingleton<ConfigurationFunctions>(sp =>
        {
            var configService = sp.GetRequiredService<IConfigService>();
            var logger = Log.ForContext<ConfigurationFunctions>();
            return new ConfigurationFunctions(configService, sp, logger);
        });

        // Function Registry（单例）
        services.AddSingleton<IFunctionRegistry>(sp =>
        {
            var proactiveFunctions = sp.GetRequiredService<ProactiveMessagingFunctions>();
            var knowledgeFunctions = sp.GetRequiredService<KnowledgeBaseFunctions>();
            var configFunctions = sp.GetRequiredService<ConfigurationFunctions>();
            var logger = Log.ForContext<FunctionRegistry>();

            return new FunctionRegistry(proactiveFunctions, knowledgeFunctions, configFunctions, logger);
        });

        // Prompt 服务（单例）
        services.AddSingleton<IPromptService, PromptService>();

        // AI 对话服务（单例，共享配置）
        services.AddSingleton<IChatService>(sp =>
        {
            var configService = sp.GetRequiredService<IConfigService>();
            var functionRegistry = sp.GetRequiredService<IFunctionRegistry>();
            var promptService = sp.GetRequiredService<IPromptService>();
            var config = configService.Load();
            Log.Information("AI 服务初始化，模型: {Model}, FunctionCalling: {Enabled}",
                config.Model, config.EnableFunctionCalling);
            return new OpenAIChatService(config, promptService, functionRegistry);
        });

        // 对话历史服务（单例）
        services.AddSingleton<IConversationHistoryService>(sp =>
        {
            var chatService = sp.GetService<IChatService>();
            var promptService = sp.GetRequiredService<IPromptService>();
            var configService = sp.GetService<IConfigService>();
            var platformPathService = sp.GetRequiredService<IPlatformPathService>();
            var config = configService?.Load();
            var service = new ConversationHistoryService(chatService, promptService, platformPathService);
            if (config != null)
            {
                service.UpdateSecondaryConfig(config);
            }
            Log.Information("对话历史服务初始化");
            return service;
        });

        // ViewModels
        services.AddTransient<MainWindowViewModel>(sp =>
        {
            var chatService = sp.GetService<IChatService>();
            var configService = sp.GetService<IConfigService>();
            var taskScheduler = sp.GetService<ITaskScheduler>();
            var historyService = sp.GetService<IConversationHistoryService>();
            var promptService = sp.GetService<IPromptService>();
            var logService = sp.GetService<ILogService>();
            var knowledgeBaseService = sp.GetService<IKnowledgeBaseService>();
            var embeddingService = sp.GetService<IEmbeddingService>();

            return new MainWindowViewModel(
                chatService,
                configService,
                taskScheduler,
                historyService,
                promptService,
                logService,
                knowledgeBaseService,
                embeddingService);
        });

        Log.Debug("依赖注入服务配置完成");
    }

    private void DisableAvaloniaDataAnnotationValidation()
    {
        // Get an array of plugins to remove
        var dataValidationPluginsToRemove =
            BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();

        // remove each entry found
        foreach (var plugin in dataValidationPluginsToRemove)
        {
            BindingPlugins.DataValidators.Remove(plugin);
        }
    }
}
