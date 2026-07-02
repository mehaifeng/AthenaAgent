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

        _wanderTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
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
