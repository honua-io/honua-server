namespace FieldDataCollection.Services;

/// <summary>
/// Service for application navigation.
/// Provides strongly-typed navigation methods and parameter passing.
/// </summary>
public interface INavigationService
{
    /// <summary>
    /// Navigates to the specified route.
    /// </summary>
    /// <param name="route">The route to navigate to.</param>
    Task GoToAsync(string route);

    /// <summary>
    /// Navigates to the specified route with parameters.
    /// </summary>
    /// <param name="route">The route to navigate to.</param>
    /// <param name="parameters">Navigation parameters.</param>
    Task GoToAsync(string route, IDictionary<string, object> parameters);

    /// <summary>
    /// Navigates back to the previous page.
    /// </summary>
    Task GoBackAsync();

    /// <summary>
    /// Navigates to the record detail page.
    /// </summary>
    /// <param name="recordId">ID of the record to view/edit.</param>
    /// <param name="mode">Edit mode (view, edit, create).</param>
    Task GoToRecordDetailAsync(string? recordId = null, RecordEditMode mode = RecordEditMode.View);

    /// <summary>
    /// Navigates to the map page.
    /// </summary>
    /// <param name="focusLocation">Optional location to focus on.</param>
    Task GoToMapAsync(Location? focusLocation = null);

    /// <summary>
    /// Navigates to the sync center page.
    /// </summary>
    Task GoToSyncCenterAsync();

    /// <summary>
    /// Navigates to the settings page.
    /// </summary>
    Task GoToSettingsAsync();
}

/// <summary>
/// Enumeration of record editing modes.
/// </summary>
public enum RecordEditMode
{
    View,
    Edit,
    Create
}