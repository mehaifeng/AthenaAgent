using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace Athena.UI.ViewModels;

/// <summary>菜单里的一项。命令随项带着：弹出菜单在独立的弹层里，<c>$parent</c> 够不到会话的 DataContext。</summary>
public sealed record ReasoningEffortChoice(ReasoningEffort Value, string Label, bool IsSelected, ICommand Command);

/// <summary>
/// 输入框底部、模型名旁边的推理强度选择。只在 OpenRouter 目录给出了这个模型的
/// <c>reasoning.supported_efforts</c> 时出现：没有佐证就不让选，因为发一个模型不认的档位会被端点直接拒掉，
/// 而那一轮失败看上去和这个按钮毫无关系。
///
/// 它不是新设置：写的就是设置页「模型元数据」里同一个 <see cref="ModelMetadataOverrides.ReasoningEffort"/>，
/// 按（供应商, 模型）存，对用这个模型的所有会话生效；请求快照按回合构建，所以改动从下一轮起作用。
/// </summary>
public sealed partial class ReasoningEffortPickerViewModel : ObservableObject
{
    private readonly IConfigService? _configService;
    private readonly Func<string, string, string> _getString;
    private readonly ILogger _logger = Log.ForContext<ReasoningEffortPickerViewModel>();
    private ResolvedModelMetadata? _metadata;

    public ReasoningEffortPickerViewModel(IConfigService? configService, Func<string, string, string> getString)
    {
        _configService = configService;
        _getString = getString;
    }

    [ObservableProperty]
    private bool _isAvailable;

    [ObservableProperty]
    private ReasoningEffort _current = ReasoningEffort.Auto;

    [ObservableProperty]
    private string _label = string.Empty;

    [ObservableProperty]
    private string _tip = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<ReasoningEffortChoice> _choices = [];

    /// <summary>主对话模型或其元数据变化时由会话调用；也用于语言切换后重算文字。</summary>
    public void Refresh(ResolvedModelMetadata? metadata)
    {
        _metadata = metadata;
        var config = _configService?.Load();
        // 回合进行中换了主模型时，会话手里的元数据还是旧模型的；档位不能拿旧模型的去显示。
        var efforts = metadata != null && IsMainModel(config, metadata) ? metadata.ReasoningEfforts : null;
        var profile = efforts != null ? FindProfile(config, metadata!) : null;
        Current = profile?.Overrides.ReasoningEffort ?? ReasoningEffort.Auto;
        IsAvailable = efforts != null;
        if (efforts == null)
        {
            Choices = [];
            Label = string.Empty;
            Tip = string.Empty;
            return;
        }

        // 已经在设置页里选了一个目录没列的档位时也照实列出来：藏掉它，按钮上的字就和真正发出去的对不上了。
        var values = efforts.Supported.Contains(Current) || Current == ReasoningEffort.Auto
            ? efforts.Supported
            : efforts.Supported.Append(Current).OrderByDescending(value => value).ToList();
        Choices = new[] { ReasoningEffort.Auto }
            .Concat(values)
            .Select(value => new ReasoningEffortChoice(value, FormatLabel(value, efforts.Default), value == Current, SelectCommand))
            .ToList();
        Label = FormatLabel(Current, efforts.Default);
        Tip = string.Format(
            _getString("Chat.ReasoningEffort.Tip", "Reasoning effort. Levels come from the OpenRouter catalog ({0}); applies to every conversation using this model."),
            efforts.OpenRouterModelId);
    }

    [RelayCommand]
    private async Task SelectAsync(ReasoningEffort effort)
    {
        if (_configService == null || _metadata == null)
        {
            _logger.Warning("Reasoning effort picker has no config service or metadata; selection {Effort} ignored", effort);
            return;
        }

        // 只写菜单里列出的档位：没有目录佐证的档位正是这个选择器存在的理由要挡住的东西。
        if (!IsAvailable || Choices.All(choice => choice.Value != effort))
        {
            _logger.Warning("Reasoning effort {Effort} is not offered for {Model}; selection ignored", effort, _metadata.ExternalModelId);
            return;
        }

        try
        {
            var config = _configService.Load();
            // 元数据和当前配置指的不是同一个模型（刚切了模型、刷新还没到），宁可不写，也不写错模型。
            if (!IsMainModel(config, _metadata))
            {
                _logger.Warning("Reasoning effort picker metadata is for {Model}, config main model is {ConfigModel}; selection ignored",
                    _metadata.ExternalModelId, config.AiModels.MainConversation.Model);
                return;
            }

            var profile = FindProfile(config, _metadata);
            if (profile == null)
            {
                if (effort == ReasoningEffort.Auto) return;
                profile = new ProviderModelMetadataProfile
                {
                    ProviderId = _metadata.ProviderId,
                    ExternalModelId = _metadata.ExternalModelId
                };
                config.AiModels.ModelMetadataProfiles.Add(profile);
            }

            if (profile.Overrides.ReasoningEffort == effort) return;
            profile.Overrides.ReasoningEffort = effort;
            await _configService.SaveAsync(config);
            Refresh(_metadata);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Saving reasoning effort {Effort} failed", effort);
        }
    }

    private string FormatLabel(ReasoningEffort value, ReasoningEffort? defaultEffort)
    {
        if (value != ReasoningEffort.Auto)
            return _getString($"ProviderModels.ReasoningEffort.{value}", value.ToString());
        return defaultEffort is { } known
            ? string.Format(_getString("Chat.ReasoningEffort.Default", "Default ({0})"), known.ToString().ToLowerInvariant())
            : _getString("Chat.ReasoningEffort.DefaultUnknown", "Default");
    }

    private static bool IsMainModel(AppConfig? config, ResolvedModelMetadata metadata) =>
        config != null
        && string.Equals(config.AiModels.MainConversation.ProviderId, metadata.ProviderId, StringComparison.Ordinal)
        && string.Equals(config.AiModels.MainConversation.Model, metadata.ExternalModelId, StringComparison.Ordinal);

    private static ProviderModelMetadataProfile? FindProfile(AppConfig? config, ResolvedModelMetadata metadata) =>
        config?.AiModels.ModelMetadataProfiles.FirstOrDefault(profile =>
            string.Equals(profile.ProviderId, metadata.ProviderId, StringComparison.Ordinal)
            && string.Equals(profile.ExternalModelId, metadata.ExternalModelId, StringComparison.Ordinal));
}
