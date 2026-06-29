using System.Windows.Controls;
using AurumTweaks.ViewModels;

namespace AurumTweaks.Views;

public partial class ProfilesView : UserControl
{
    public ProfilesView()
    {
        InitializeComponent();
        // The VM is a singleton that builds its profile list once at construction; rebuild it whenever the page
        // is shown so a profile just saved from the Tweaks page appears without restarting the app.
        Loaded += async (_, _) =>
        {
            if (DataContext is ProfilesViewModel vm) await vm.RefreshAsync();
        };
    }
}
