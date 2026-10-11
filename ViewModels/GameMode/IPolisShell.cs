using Athena.UI.Models;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Athena.UI.ViewModels.GameMode;

/// <summary>
/// 游戏模式能请外壳做的事。外壳（<see cref="MainWindowViewModel"/>）在运行时把自己挂上来（<see cref="GameModeViewModel.AttachShell"/>），
/// 依赖只朝一个方向：游戏不持有会话树，会话树是唯一的选择来源（设计稿 9.2）——点橄榄树、开新委托，
/// 走的都是外壳里"选中会话 / 新建会话"的同一条路径。
/// </summary>
public interface IPolisShell
{
    /// <summary>会话树上当前选中的会话。</summary>
    ConversationSessionItemViewModel? SelectedSession { get; }

    /// <summary>会话树上的全部会话（意图校验时核对会话 Id 用）。</summary>
    IEnumerable<ConversationSessionItemViewModel> AllSessions { get; }

    /// <summary>选中一个会话：与在左侧点它完全相同。找不到返回 false。</summary>
    bool SelectConversation(string conversationId);

    /// <summary>在这个工作区（null = 全局对话）里开一份新委托：与会话树上的"新建会话"完全相同。</summary>
    Task CreateCommissionAsync(WorkspaceProfile? workspace);

    /// <summary>在工作台里打开一个（已校验在工作区内的）文件。</summary>
    Task OpenFileAsync(string fullPath);

    /// <summary>把一个（已校验在工作区内的）文件放进当前会话的待发送附件——走输入框的同一条附件路径。</summary>
    Task AttachFileAsync(string fullPath);

    /// <summary>预填当前会话的输入框并把焦点给它；不发送。</summary>
    void PrefillInput(string text);

    /// <summary>停止当前会话正在进行的回合。</summary>
    void StopCurrentTurn();

    /// <summary>挑一个文件夹，重新定位这个工作区（只改目录，Id 不变）。成功返回 true。</summary>
    Task<bool> PickAndRelocateWorkspaceAsync(WorkspaceProfile workspace);

    /// <summary>重新定位成功之后发布。</summary>
    event EventHandler<WorkspaceProfile>? WorkspaceRelocated;
}

/// <summary>视图交给 ViewModel 的那一端：往页面里推一段脚本。ViewModel 不认识 NativeWebView。</summary>
public interface IPolisPageChannel
{
    Task InvokeScriptAsync(string script);
}
