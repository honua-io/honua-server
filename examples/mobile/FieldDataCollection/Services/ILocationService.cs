namespace FieldDataCollection.Services;

/// <summary>
/// Service for managing device location services.
/// Provides access to current location, location accuracy, and GPS status.
/// </summary>
public interface ILocationService
{
    /// <summary>
    /// Gets the current device location.
    /// </summary>
    /// <returns>Current location or null if unavailable.</returns>
    Task<Location?> GetCurrentLocationAsync();

    /// <summary>
    /// Gets the current location with specific accuracy requirements.
    /// </summary>
    /// <param name="accuracy">Desired location accuracy.</param>
    /// <param name="timeout">Timeout for location request.</param>
    /// <returns>Current location or null if unavailable within timeout.</returns>
    Task<Location?> GetCurrentLocationAsync(GeolocationAccuracy accuracy, TimeSpan timeout);

    /// <summary>
    /// Checks if location services are enabled and permissions are granted.
    /// </summary>
    /// <returns>True if location services are available.</returns>
    Task<bool> IsLocationAvailableAsync();

    /// <summary>
    /// Requests location permissions from the user.
    /// </summary>
    /// <returns>True if permissions were granted.</returns>
    Task<bool> RequestLocationPermissionAsync();

    /// <summary>
    /// Gets the current GPS accuracy status.
    /// </summary>
    /// <returns>Current accuracy in meters, or null if unknown.</returns>
    Task<double?> GetLocationAccuracyAsync();

    /// <summary>
    /// Event raised when location accuracy changes.
    /// </summary>
    event EventHandler<LocationAccuracyChangedEventArgs>? LocationAccuracyChanged;
}

/// <summary>
/// Event arguments for location accuracy changes.
/// </summary>
public class LocationAccuracyChangedEventArgs : EventArgs
{
    public double? AccuracyMeters { get; init; }
    public DateTime Timestamp { get; init; }
}