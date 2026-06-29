using System.Windows.Controls;
using AurumTweaks.ViewModels;

namespace AurumTweaks.Views;

public partial class JournalView : UserControl
{
    public JournalView()
    {
        InitializeComponent();
        // The VM is a singleton that loads the trail once at construction; reload it whenever the page is shown so
        // a batch just applied from the Tweaks page appears without restarting the app.
        Loaded += async (_, _) =>
        {
            if (DataContext is JournalViewModel vm) await vm.RefreshAsync();
        };
    }
}
