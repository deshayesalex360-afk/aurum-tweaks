using System.Windows;

namespace AurumTweaks.Views;

public partial class SplashWindow : Window
{
    public SplashWindow()
    {
        InitializeComponent();
    }

    public void SetStatus(string status)
    {
        if (Dispatcher.CheckAccess()) StatusText.Text = status;
        else Dispatcher.Invoke(() => StatusText.Text = status);
    }
}
