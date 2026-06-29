using System.Windows.Controls;
using AurumTweaks.ViewModels;

namespace AurumTweaks.Views;

public partial class TransparencyView : UserControl
{
    public TransparencyView()
    {
        InitializeComponent();

        // Re-read the live facts each time the page is shown: the « point de restauration » line must reflect the
        // CURRENT setting the user may have toggled in Paramètres, never a value frozen when the singleton was built.
        Loaded += (_, _) =>
        {
            if (DataContext is TransparencyViewModel vm && vm.RefreshCommand.CanExecute(null))
                vm.RefreshCommand.Execute(null);
        };
    }
}
