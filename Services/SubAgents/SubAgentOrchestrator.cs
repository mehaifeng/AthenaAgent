using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using Athena.UI.ViewModels;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.SubAgents;

/// <summary>
/// 子代理编排器。负责：批量并行派生子代理、并发上限、副作用串行（全局工具闸）、
/// 取消联动、结果合并回传，以及把每只猫头鹰的实时状态暴露给 UI 绑定。
/// </summary>
public sealed class SubAgentOrchestrator : ISubAgentOrchestrator
{
    private readonly IConfigService _configService;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger _logger;

    // 分类工具闸：仅串行有竞态风险的工具，其余并行。避免长耗时浏览器任务堵死快速只读工具。
    private readonly SemaphoreSlim _browserGate = new(1, 1);  // 单 Playwright 会话，浏览器任务互斥
    private readonly SemaphoreSlim _writeGate = new(1, 1);    // 文件系统 / 知识库向量库写入互斥

    private SemaphoreSlim? GateFor(string toolName) => SubAgentToolGates.Category(toolName) switch
    {
        ToolGateCategory.Browser => _browserGate,
        ToolGateCategory.Write => _writeGate,
        _ => null
    };

    public ObservableCollection<SubAgentViewModel> ActiveAgents { get; } = new();

    public SubAgentOrchestrator(IConfigService configService, IServiceProvider serviceProvider, ILogger logger)
    {
        _configService = configService;
        _serviceProvider = serviceProvider;
        _logger = logger.ForContext<SubAgentOrchestrator>();
    }

    public async Task<string> DispatchBatchAsync(SubAgentTaskInput[] tasks, CancellationToken cancellationToken)
    {
        if (tasks == null || tasks.Length == 0)
        {
            return "No sub-agent tasks were provided.";
        }

        // 惰性解析 IFunctionRegistry，断开 Registry → SubAgentFunctions → Orchestrator → Registry 的构造环。
        var functionRegistry = _serviceProvider.GetRequiredService<IFunctionRegistry>();

        var config = _configService.Load();
        var maxParallel = Math.Max(1, config.SubAgentMaxParallel);
        var timeoutSeconds = Math.Max(1, config.SubAgentTimeoutSeconds);
        using var concurrencyGate = new SemaphoreSlim(maxParallel, maxParallel);

        var runs = new List<(SubAgentViewModel Vm, SubAgentTaskInput Task)>(tasks.Length);
        foreach (var task in tasks)
        {
            // 每只猫头鹰一个独立取消源：联动批次令牌（"停止"）+ 自身超时上限。
            var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            var vm = new SubAgentViewModel
            {
                Title = string.IsNullOrWhiteSpace(task.Title) ? "subtask" : task.Title,
                AgentType = SubAgentTypes.Resolve(task.AgentType).Name,
                State = SubAgentState.Pending,
                CurrentAction = "queued…",
                Cts = cts
            };
            runs.Add((vm, task));
            Post(() => ActiveAgents.Add(vm));
        }

        _logger.Information("Sub-agent batch dispatched: {Count} task(s), maxParallel={MaxParallel}", tasks.Length, maxParallel);

        var running = runs.Select(async run =>
        {
            await concurrencyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var runner = new SubAgentRunner(_configService, functionRegistry, _logger);
                return await runner.RunAsync(run.Task, run.Vm, GateFor, Post, run.Vm.Cts!.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new SubAgentResult
                {
                    Title = run.Task.Title,
                    AgentType = run.Task.AgentType,
                    Success = false,
                    Summary = "Cancelled."
                };
            }
            finally
            {
                concurrencyGate.Release();
            }
        }).ToList();

        SubAgentResult[] results;
        try
        {
            results = await Task.WhenAll(running).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            results = runs
                .Select(r => new SubAgentResult { Title = r.Task.Title, AgentType = r.Task.AgentType, Success = false, Summary = "Cancelled." })
                .ToArray();
        }

        return MergeResults(results);
    }

    public void ClearCompleted()
    {
        Post(() =>
        {
            for (var i = ActiveAgents.Count - 1; i >= 0; i--)
            {
                var state = ActiveAgents[i].State;
                if (state is SubAgentState.Done or SubAgentState.Error or SubAgentState.Cancelled)
                {
                    ActiveAgents.RemoveAt(i);
                }
            }
        });
    }

    private static string MergeResults(SubAgentResult[] results)
    {
        var sb = new StringBuilder();
        sb.Append("Dispatched ").Append(results.Length).Append(" sub-agent(s). Results below.\n");
        for (var i = 0; i < results.Length; i++)
        {
            var r = results[i];
            sb.Append("\n--- [").Append(i + 1).Append("] ").Append(r.Title)
              .Append(" (").Append(r.AgentType).Append(") — ")
              .Append(r.Success ? "OK" : "FAILED").Append(" ---\n")
              .Append(r.Summary).Append('\n');
        }
        return sb.ToString();
    }

    // 所有对 ObservableCollection / ViewModel 的写操作都经 UI 线程，保证 Avalonia 绑定线程安全。
    private static void Post(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }
}
