using Athena.UI.Models;

namespace Athena.UI.Services;

/// <summary>
/// 工具被审批闸门拦下时，主模型收到的那句话。按裁决来源分开写。
///
/// 此前不分来源，一律是「用户拒绝了……请勿重试」：审批模型自己的拒绝、它的技术故障、静态策略的拦截
/// 都这么说。主模型于是向用户转述一个没有发生过的拒绝，对一次偶发的截断也采取「放弃这条路」的应对
/// （2026-10-04：审批模型输出被截断，主模型收到的是「用户拒绝了 fetch_url_to_file」）。
/// </summary>
public static class ToolApprovalDenialMessage
{
    public static string Build(string functionName, ToolApprovalDecision decision) => decision.Source switch
    {
        ToolApprovalSource.User =>
            $"用户拒绝了工具调用 '{functionName}'（原因：{decision.Reason}）。请勿重试该调用；改用其他方式，或向用户说明为何需要此操作并征得同意。",
        ToolApprovalSource.JudgeVerdict =>
            $"工具调用 '{functionName}' 未执行：{decision.Reason}。这是自动审批模型的判断，不是用户本人的决定。"
            + "请勿原样重试；改用其他方式，或向用户说明为何需要此操作——用户在对话中明确同意后，可以再次发起同一调用。",
        ToolApprovalSource.JudgeFailure => decision.FailureKind switch
        {
            ToolApprovalJudgeFailureKind.Output =>
                $"工具调用 '{functionName}' 未执行：{decision.Reason}。原因是自动审批模型这次没有给出可用的裁决，"
                + "并不是用户或审批模型认为这个调用不该做。可以原样重试一次；如果再次失败，请告诉用户自动审批暂时不可用，不要继续尝试同一调用。",
            ToolApprovalJudgeFailureKind.Transport =>
                $"工具调用 '{functionName}' 未执行：{decision.Reason}。审批请求没能完成，这是技术故障，"
                + "并不是用户或审批模型认为这个调用不该做。请不要立刻重试；告诉用户自动审批暂时不可用，等用户回应后再决定是否继续。",
            _ =>
                $"工具调用 '{functionName}' 未执行：{decision.Reason}。自动审批模型当前不可用（配置问题），"
                + "并不是用户或审批模型认为这个调用不该做，重试不会改变结果。请告诉用户检查设置中的自动审批模型。",
        },
        ToolApprovalSource.Cancelled =>
            $"工具调用 '{functionName}' 未执行：本轮执行已被取消（{decision.Reason}）。",
        _ =>
            $"工具调用 '{functionName}' 被安全策略拦下（原因：{decision.Reason}）。请勿重试该调用；改用其他方式，或向用户说明为何需要此操作。",
    };
}
