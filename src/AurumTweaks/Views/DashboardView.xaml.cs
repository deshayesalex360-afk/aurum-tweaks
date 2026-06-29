using System.Windows.Controls;
using AurumTweaks.ViewModels;

namespace AurumTweaks.Views;

public partial class DashboardView : UserControl
{
    public DashboardView()
    {
        InitializeComponent();
        // The dashboard VM is a singleton, so its recent-activity card is otherwise filled once. Refresh it on
        // show so a batch applied from the Tweaks/Profiles page appears here without an app restart.
        Loaded += async (_, _) =>
        {
            if (DataContext is DashboardViewModel vm)
                await vm.RefreshRecentActivityAsync();
        };
    }
}
