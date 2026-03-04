// using CommunityToolkit.Maui.Alerts;
// using CommunityToolkit.Maui.Core;

namespace FieldDataCollection.Services;

/// <summary>
/// Implementation of IDialogService using MAUI and CommunityToolkit.Maui.
/// </summary>
public class DialogService : IDialogService
{
    public async Task DisplayAlertAsync(string title, string message, string cancel = "OK")
    {
        var mainPage = Application.Current?.MainPage;
        if (mainPage != null)
        {
            await mainPage.DisplayAlert(title, message, cancel);
        }
    }

    public async Task<bool> DisplayConfirmAsync(string title, string message, string accept = "OK", string cancel = "Cancel")
    {
        var mainPage = Application.Current?.MainPage;
        if (mainPage != null)
        {
            return await mainPage.DisplayAlert(title, message, accept, cancel);
        }
        return false;
    }

    public async Task<string?> DisplayPromptAsync(string title, string message, string accept = "OK", string cancel = "Cancel", string? placeholder = null, string? initialValue = null)
    {
        var mainPage = Application.Current?.MainPage;
        if (mainPage != null)
        {
            return await mainPage.DisplayPromptAsync(title, message, accept, cancel, placeholder, initialValue: initialValue);
        }
        return null;
    }

    public async Task<string?> DisplayActionSheetAsync(string title, string cancel, string? destruction = null, params string[] buttons)
    {
        var mainPage = Application.Current?.MainPage;
        if (mainPage != null)
        {
            return await mainPage.DisplayActionSheet(title, cancel, destruction, buttons);
        }
        return null;
    }

    public void ShowLoading(string? message = null)
    {
        // Implementation would depend on loading UI component
        // For now, this is a placeholder
    }

    public void HideLoading()
    {
        // Implementation would depend on loading UI component
        // For now, this is a placeholder
    }

    public async Task ShowToastAsync(string message, ToastDuration duration = ToastDuration.Short)
    {
        try
        {
            // Fallback to alert since CommunityToolkit.Maui not available for .NET 10 yet
            await DisplayAlertAsync("Notice", message);
        }
        catch (Exception)
        {
            // Fallback to alert if toast fails
            await DisplayAlertAsync("Notice", message);
        }
    }
}