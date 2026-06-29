using System;

namespace AurumTweaks.Services;

public sealed class NavigationService : INavigationService
{
    public string CurrentPage { get; private set; } = "Dashboard";

    public event EventHandler<string>? Navigated;

    public void NavigateTo(string pageKey)
    {
        if (string.IsNullOrWhiteSpace(pageKey) || pageKey == CurrentPage) return;
        CurrentPage = pageKey;
        Navigated?.Invoke(this, pageKey);
    }
}
