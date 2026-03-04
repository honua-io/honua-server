namespace FieldDataCollection.Services;

/// <summary>
/// Implementation of ILocationService using Microsoft.Maui.Essentials.
/// </summary>
public class LocationService : ILocationService
{
    private GeolocationRequest? _lastRequest;

    public event EventHandler<LocationAccuracyChangedEventArgs>? LocationAccuracyChanged;

    public async Task<Location?> GetCurrentLocationAsync()
    {
        return await GetCurrentLocationAsync(GeolocationAccuracy.Medium, TimeSpan.FromSeconds(30));
    }

    public async Task<Location?> GetCurrentLocationAsync(GeolocationAccuracy accuracy, TimeSpan timeout)
    {
        try
        {
            if (!await IsLocationAvailableAsync())
            {
                return null;
            }

            var request = new GeolocationRequest
            {
                DesiredAccuracy = accuracy,
                Timeout = timeout
            };

            _lastRequest = request;

            var location = await Geolocation.GetLocationAsync(request);

            // Notify accuracy change
            if (location != null)
            {
                LocationAccuracyChanged?.Invoke(this, new LocationAccuracyChangedEventArgs
                {
                    AccuracyMeters = location.Accuracy,
                    Timestamp = DateTime.Now
                });
            }

            return location;
        }
        catch (FeatureNotSupportedException)
        {
            // Location is not supported on this device
            return null;
        }
        catch (FeatureNotEnabledException)
        {
            // Location is not enabled on this device
            return null;
        }
        catch (PermissionException)
        {
            // Location permission not granted
            return null;
        }
        catch (Exception)
        {
            // Other error occurred
            return null;
        }
    }

    public async Task<bool> IsLocationAvailableAsync()
    {
        try
        {
            var status = await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>();
            return status == PermissionStatus.Granted;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task<bool> RequestLocationPermissionAsync()
    {
        try
        {
            var status = await Permissions.RequestAsync<Permissions.LocationWhenInUse>();
            return status == PermissionStatus.Granted;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task<double?> GetLocationAccuracyAsync()
    {
        var location = await GetCurrentLocationAsync();
        return location?.Accuracy;
    }
}