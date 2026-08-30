using Athena.UI.Services.Interfaces;

namespace Athena.UI.ViewModels;

/// <summary>
/// 猫头鹰呈现对象的工厂。编排器需要为每个子代理任务创建一只，但它在服务层，
/// 不能 <c>new</c> 一个 ViewModel——那正是 <see cref="ISubAgentProgress"/> 要断开的方向。
/// 依赖方向因此变成 ViewModels → Services（实现接口），而不是 Services → ViewModels。
/// </summary>
public sealed class SubAgentPresenterFactory : ISubAgentPresenterFactory
{
    private readonly ILocalizationService? _localizationService;

    public SubAgentPresenterFactory(ILocalizationService? localizationService = null)
    {
        _localizationService = localizationService;
    }

    public ISubAgentProgress Create() => new SubAgentViewModel(_localizationService);
}
