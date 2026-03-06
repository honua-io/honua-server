// Copyright (c) 2026 Honua Project Contributors
// Licensed under the Apache License, Version 2.0 (the "License");

namespace HonuaFieldApp.Services;

/// <summary>
/// Service for GPS location operations with device-specific optimizations.
/// </summary>
public interface IGpsLocationService
{
    /// <summary>
    /// Get the current device location with specified accuracy requirements.
    /// </summary>
    /// <param name="request">Geolocation request parameters</param>
    /// <returns>Current location or null if unavailable</returns>
    Task<Location?> GetCurrentLocationAsync(GeolocationRequest request);

    /// <summary>
    /// Start continuous location tracking for field data collection.
    /// </summary>
    /// <param name="accuracyMeters">Required accuracy in meters</param>
    /// <param name="updateInterval">Update interval for location tracking</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Stream of location updates</returns>
    IAsyncEnumerable<Location> StartLocationTrackingAsync(
        double accuracyMeters,
        TimeSpan updateInterval,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stop location tracking to conserve battery.
    /// </summary>
    Task StopLocationTrackingAsync();

    /// <summary>
    /// Check if location services are available and enabled.
    /// </summary>
    Task<bool> IsLocationAvailableAsync();

    /// <summary>
    /// Get the device's location accuracy capability.
    /// </summary>
    Task<double> GetLocationAccuracyAsync();
}

/// <summary>
/// Implementation of GPS location service using MAUI Essentials.
/// </summary>
public class GpsLocationService : IGpsLocationService
{
    private readonly ILogger<GpsLocationService> _logger;
    private CancellationTokenSource? _trackingCancellation;
    private bool _isTracking;

    public GpsLocationService(ILogger<GpsLocationService> logger)
    {
        _logger = logger;
    }

    public async Task<Location?> GetCurrentLocationAsync(GeolocationRequest request)
    {
        try
        {
            _logger.LogDebug("Requesting current location with accuracy: {Accuracy}", request.DesiredAccuracy);

            var location = await Geolocation.GetLocationAsync(request);

            if (location != null)
            {
                _logger.LogDebug("Got location: {Lat}, {Lon}, accuracy: {Accuracy}m",
                    location.Latitude, location.Longitude, location.Accuracy ?? 0);
            }
            else
            {
                _logger.LogWarning("Failed to get current location");
            }

            return location;
        }
        catch (FeatureNotSupportedException ex)
        {
            _logger.LogError(ex, "Geolocation is not supported on this device");
            return null;
        }
        catch (FeatureNotEnabledException ex)
        {
            _logger.LogError(ex, "Geolocation is not enabled on this device");
            return null;
        }
        catch (PermissionException ex)
        {
            _logger.LogError(ex, "Geolocation permission not granted");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting current location");
            return null;
        }
    }

    public async IAsyncEnumerable<Location> StartLocationTrackingAsync(
        double accuracyMeters,
        TimeSpan updateInterval,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (_isTracking)
        {
            throw new InvalidOperationException("Location tracking is already active");
        }

        _isTracking = true;
        _trackingCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var accuracy = accuracyMeters switch
        {
            <= 3 => GeolocationAccuracy.Best,
            <= 10 => GeolocationAccuracy.High,
            <= 100 => GeolocationAccuracy.Medium,
            _ => GeolocationAccuracy.Low
        };

        _logger.LogInformation("Starting location tracking with {Accuracy} accuracy, {Interval}ms interval",
            accuracy, updateInterval.TotalMilliseconds);

        try
        {
            while (!_trackingCancellation.Token.IsCancellationRequested)
            {
                var request = new GeolocationRequest(accuracy, updateInterval);
                var location = await GetCurrentLocationAsync(request);

                if (location != null)
                {
                    yield return location;
                }

                await Task.Delay(updateInterval, _trackingCancellation.Token);
            }
        }
        finally
        {
            _isTracking = false;
            _trackingCancellation?.Dispose();
            _trackingCancellation = null;
        }
    }

    public async Task StopLocationTrackingAsync()
    {
        if (_isTracking && _trackingCancellation != null)
        {
            _logger.LogInformation("Stopping location tracking");
            _trackingCancellation.Cancel();
            await Task.Delay(100); // Give time for cleanup
        }
    }

    public async Task<bool> IsLocationAvailableAsync()
    {
        try
        {
            return await Geolocation.GetLocationAsync(new GeolocationRequest
            {
                DesiredAccuracy = GeolocationAccuracy.Low,
                Timeout = TimeSpan.FromSeconds(1)
            }) != null;
        }
        catch
        {
            return false;
        }
    }

    public async Task<double> GetLocationAccuracyAsync()
    {
        try
        {
            var location = await Geolocation.GetLocationAsync(new GeolocationRequest(
                GeolocationAccuracy.Best, TimeSpan.FromSeconds(5)));
            return location?.Accuracy ?? double.MaxValue;
        }
        catch
        {
            return double.MaxValue;
        }
    }
}