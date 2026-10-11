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

    /// <summary>会话树上的全部会话（意图校验时核对会话 Id 用；橄榄林、居民、海图从这里推导）。</summary>
    IEnumerable<ConversationSessionItemViewModel> AllSessions { get; }

    /// <summary>会话树上的全部工作区（海图上一座岛一座城，包括还没有会话的工作区）。</summary>
    IReadOnlyList<WorkspaceProfile> Workspaces { get; }

    /// <summary>选中一个会话：与在左侧点它完全相同。找不到返回 false。</summary>
    bool SelectConversation(string conversationId);

    /// <summary>
    /// 在这个工作区（null = 全局对话）里开一份新委托：与会话树上的"新建会话"完全相同。返回新开的会话
    /// （委托板、神谕、居民接着往它的输入框里预填）；开不了返回 null。
    /// </summary>
    Task<ConversationSessionItemViewModel?> CreateCommissionAsync(WorkspaceProfile? workspace);

    /// <summary>在工作台里打开一个（已校验在工作区内的）文件。</summary>
    Task OpenFileAsync(string fullPath);

    /// <summary>把一个（已校验在工作区内的）文件放进当前会话的待发送附件——走输入框的同一条附件路径。</summary>
    Task AttachFileAsync(string fullPath);

    /// <summary>
    /// 预填某个会话的输入框并请求焦点；不发送。按会话 Id 指定，而不是"当前显示的那个"：选中一个会话之后，
    /// 外壳要等第二拍（选中动效跑完）才换中间的输入框，紧跟着预填"当前的"会落到上一个会话里。
    /// </summary>
    void PrefillInput(string conversationId, string text);

    /// <summary>
    /// 城里对话栏的"托付"（2026-10-11 起游戏盖住了输入框）：把这段话放进这个会话的输入框、执行它自己的发送命令——
    /// 和你在输入框里按发送是同一条路径（附件、上下文、压缩都照旧）。只对会话树上选中的那个会话生效；
    /// 它正在回答、正在压缩、或者话是空的时候发送命令不可执行，返回 false，什么都不发。
    /// </summary>
    bool SubmitInput(string conversationId, string text);

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
