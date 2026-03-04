namespace FieldDataCollection.Services;

/// <summary>
/// Service for displaying user dialogs and alerts.
/// Provides cross-platform dialog functionality with async/await support.
/// </summary>
public interface IDialogService
{
    /// <summary>
    /// Displays an alert dialog with a single OK button.
    /// </summary>
    /// <param name="title">Dialog title.</param>
    /// <param name="message">Dialog message.</param>
    /// <param name="cancel">Text for the cancel button (default: "OK").</param>
    Task DisplayAlertAsync(string title, string message, string cancel = "OK");

    /// <summary>
    /// Displays a confirmation dialog with OK and Cancel buttons.
    /// </summary>
    /// <param name="title">Dialog title.</param>
    /// <param name="message">Dialog message.</param>
    /// <param name="accept">Text for the accept button (default: "OK").</param>
    /// <param name="cancel">Text for the cancel button (default: "Cancel").</param>
    /// <returns>True if the user accepted, false if cancelled.</returns>
    Task<bool> DisplayConfirmAsync(string title, string message, string accept = "OK", string cancel = "Cancel");

    /// <summary>
    /// Displays a prompt dialog for text input.
    /// </summary>
    /// <param name="title">Dialog title.</param>
    /// <param name="message">Dialog message.</param>
    /// <param name="accept">Text for the accept button (default: "OK").</param>
    /// <param name="cancel">Text for the cancel button (default: "Cancel").</param>
    /// <param name="placeholder">Placeholder text for input field.</param>
    /// <param name="initialValue">Initial value for input field.</param>
    /// <returns>The entered text or null if cancelled.</returns>
    Task<string?> DisplayPromptAsync(string title, string message, string accept = "OK", string cancel = "Cancel", string? placeholder = null, string? initialValue = null);

    /// <summary>
    /// Displays an action sheet with multiple options.
    /// </summary>
    /// <param name="title">Sheet title.</param>
    /// <param name="cancel">Text for the cancel button.</param>
    /// <param name="destruction">Text for the destruction button (optional).</param>
    /// <param name="buttons">Array of action button texts.</param>
    /// <returns>The selected button text or null if cancelled.</returns>
    Task<string?> DisplayActionSheetAsync(string title, string cancel, string? destruction = null, params string[] buttons);

    /// <summary>
    /// Shows a loading indicator with optional message.
    /// </summary>
    /// <param name="message">Loading message (optional).</param>
    void ShowLoading(string? message = null);

    /// <summary>
    /// Hides the loading indicator.
    /// </summary>
    void HideLoading();

    /// <summary>
    /// Shows a toast notification.
    /// </summary>
    /// <param name="message">Toast message.</param>
    /// <param name="duration">Toast duration (default: short).</param>
    Task ShowToastAsync(string message, ToastDuration duration = ToastDuration.Short);
}

/// <summary>
/// Enumeration of toast duration options.
/// </summary>
public enum ToastDuration
{
    Short,
    Long
}