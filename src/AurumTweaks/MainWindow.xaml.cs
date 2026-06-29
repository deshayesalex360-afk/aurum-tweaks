using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using AurumTweaks.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace AurumTweaks;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<MainViewModel>();
    }

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string pageKey && DataContext is MainViewModel vm)
        {
            vm.Navigate(pageKey);
        }
    }

    private void MinBtn_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaxBtn_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

    // --- Command palette (Ctrl+K) glue. The VM owns all state and ranking; the code-behind only translates
    // raw input gestures (clicks, arrow keys) into VM intent and handles the one thing the VM can't: focus. ---

    private void OpenPalette_Click(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.Palette.Open();
    }

    private void PaletteBackdrop_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.Palette.Close();
    }

    // Arrow keys move the highlight (wrapping) and keep the highlighted row in view; Enter activates; Esc closes.
    // Handled here rather than via InputBindings so the TextBox keeps the caret while the list is driven.
    private void PaletteSearch_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        switch (e.Key)
        {
            case Key.Down:
                vm.Palette.MoveSelection(1);
                ScrollSelectedIntoView(vm);
                e.Handled = true;
                break;
            case Key.Up:
                vm.Palette.MoveSelection(-1);
                ScrollSelectedIntoView(vm);
                e.Handled = true;
                break;
            case Key.Enter:
                vm.Palette.Activate();
                e.Handled = true;
                break;
            case Key.Escape:
                vm.Palette.Close();
                e.Handled = true;
                break;
        }
    }

    private void ScrollSelectedIntoView(MainViewModel vm)
    {
        var i = vm.Palette.SelectedIndex;
        if (i >= 0 && i < vm.Palette.Results.Count)
            PaletteResultsList.ScrollIntoView(vm.Palette.Results[i]);
    }

    // When the overlay becomes visible, the TextBox can't take focus until the layout pass finishes, so defer
    // the focus+select to render priority. SelectAll makes the next keystroke replace any stale query text.
    private void PaletteSearch_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && sender is TextBox box)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Render, new System.Action(() =>
            {
                box.Focus();
                box.SelectAll();
            }));
        }
    }

    // Click-to-activate: resolve the clicked row to its index so a click both selects and opens, matching the
    // keyboard path. Walk up from the hit element to the container, since the click may land on inner text.
    private void PaletteResults_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (e.OriginalSource is not DependencyObject src) return;

        var container = ItemsControl.ContainerFromElement(PaletteResultsList, src) as ListBoxItem;
        if (container is null) return;

        var index = PaletteResultsList.ItemContainerGenerator.IndexFromContainer(container);
        if (index < 0) return;

        vm.Palette.SelectedIndex = index;
        vm.Palette.Activate();
        e.Handled = true;
    }
}
