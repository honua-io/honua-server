namespace FieldDataCollection.Services;

/// <summary>
/// Implementation of INavigationService using MAUI Shell navigation.
/// </summary>
public class NavigationService : INavigationService
{
    public async Task GoToAsync(string route)
    {
        await Shell.Current.GoToAsync(route);
    }

    public async Task GoToAsync(string route, IDictionary<string, object> parameters)
    {
        await Shell.Current.GoToAsync(route, parameters);
    }

    public async Task GoBackAsync()
    {
        await Shell.Current.GoToAsync("..");
    }

    public async Task GoToRecordDetailAsync(string? recordId = null, RecordEditMode mode = RecordEditMode.View)
    {
        var parameters = new Dictionary<string, object>
        {
            ["mode"] = mode.ToString()
        };

        if (!string.IsNullOrEmpty(recordId))
        {
            parameters["recordId"] = recordId;
        }

        await GoToAsync("RecordDetailPage", parameters);
    }

    public async Task GoToMapAsync(Location? focusLocation = null)
    {
        if (focusLocation != null)
        {
            var parameters = new Dictionary<string, object>
            {
                ["latitude"] = focusLocation.Latitude,
                ["longitude"] = focusLocation.Longitude
            };
            await GoToAsync("//MapPage", parameters);
        }
        else
        {
            await GoToAsync("//MapPage");
        }
    }

    public async Task GoToSyncCenterAsync()
    {
        await GoToAsync("//SyncCenterPage");
    }

    public async Task GoToSettingsAsync()
    {
        await GoToAsync("//SettingsPage");
    }
}