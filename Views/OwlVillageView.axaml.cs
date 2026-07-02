using Athena.UI.ViewModels;
using Avalonia.Controls;
using Avalonia.Threading;
using System;
using System.Linq;

namespace Athena.UI.Views;

public partial class OwlVillageView : UserControl
{
    // 游荡节拍：只在视图挂载（弹出打开）时运行，关闭即停，避免常驻开销。
    private readonly DispatcherTimer _wanderTimer;

    public OwlVillageView()
    {
        InitializeComponent();

        // 高频轮询、低频挪动：每只猫头鹰有自己的下次挪窝时刻（见 SubAgentViewModel.RepositionWander），
        // 这里只负责节拍触发，到点的才动，形成互不同步的随机游走。
        _wanderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _wanderTimer.Tick += (_, _) => Wander();

        AttachedToVisualTree += (_, _) =>
        {
            Wander();
            _wanderTimer.Start();
        };
        DetachedFromVisualTree += (_, _) => _wanderTimer.Stop();
    }

    private void Wander()
    {
        if (DataContext is ChatTabViewModel vm && vm.Orchestrator is { } orchestrator)
        {
            SubAgentViewModel.RepositionWander(orchestrator.ActiveAgents.ToList());
        }
    }
}
