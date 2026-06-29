using System.Windows.Controls;
using AurumTweaks.ViewModels;

namespace AurumTweaks.Views;

public partial class SnapshotView : UserControl
{
    public SnapshotView()
    {
        InitializeComponent();
        // The VM is a singleton that loads the saved snapshots once at construction; reload whenever the page is
        // shown so a capture made earlier (or a snapshot deleted) is reflected without restarting the app.
        Loaded += async (_, _) =>
        {
            if (DataContext is SnapshotViewModel vm) await vm.RefreshAsync();
        };
    }
}
