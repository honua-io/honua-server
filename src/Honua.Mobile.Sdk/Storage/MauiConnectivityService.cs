// Copyright (c) 2026 Honua Project Contributors
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using Microsoft.Extensions.Logging;
using Microsoft.Maui.Essentials;
using Honua.Mobile.Sdk.Clients;

namespace Honua.Mobile.Sdk.Storage;

/// <summary>
/// MAUI implementation of connectivity service for mobile platforms.
/// Uses MAUI.Essentials to monitor network and battery status.
/// </summary>
public class MauiConnectivityService : IConnectivityService
{
    private readonly ILogger<MauiConnectivityService> _logger;
    private NetworkConnectionType _lastConnectionType = NetworkConnectionType.None;
    private double _lastBatteryLevel = 0.0;

    /// <summary>
    /// Initializes a new instance of the MauiConnectivityService.
    /// </summary>
    /// <param name="logger">Logger instance</param>
    public MauiConnectivityService(ILogger<MauiConnectivityService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Subscribe to connectivity and battery change events
        Connectivity.ConnectivityChanged += OnConnectivityChanged;
        // Note: BatteryInfoChanged event handling would need platform-specific implementation
    }

    /// <summary>
    /// Event fired when connectivity status changes.
    /// </summary>
    public event EventHandler<ConnectivityChangedEventArgs>? ConnectivityChanged;

    /// <summary>
    /// Event fired when battery status changes significantly.
    /// </summary>
    public event EventHandler<BatteryChangedEventArgs>? BatteryChanged;

    /// <summary>
    /// Checks if a network connection is available based on the specified policy.
    /// </summary>
    /// <param name="networkPolicy">Network access policy</param>
    /// <returns>True if connection is available</returns>
    public async Task<bool> IsConnectionAvailableAsync(NetworkPolicy networkPolicy)
    {
        try
        {
            // Check if we're in offline mode
            if (networkPolicy == NetworkPolicy.Offline)
                return false;

            // Get current network access
            var networkAccess = Connectivity.NetworkAccess;

            if (networkAccess != NetworkAccess.Internet)
            {
                _logger.LogDebug("Network access not available: {NetworkAccess}", networkAccess);
                return false;
            }

            // Get connection profiles
            var profiles = Connectivity.ConnectionProfiles;
            var connectionType = await GetNetworkConnectionTypeAsync();

            // Check policy compliance
            return networkPolicy switch
            {
                NetworkPolicy.PreferWifi => connectionType == NetworkConnectionType.WiFi ||
                                          (connectionType == NetworkConnectionType.Cellular && profiles.Contains(ConnectionProfile.WiFi) == false),
                NetworkPolicy.WifiOrCellular => connectionType == NetworkConnectionType.WiFi ||
                                              connectionType == NetworkConnectionType.Cellular,
                NetworkPolicy.Any => connectionType != NetworkConnectionType.None,
                _ => false
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking network connectivity");
            return false;
        }
    }

    /// <summary>
    /// Checks if the battery level is sufficient for the specified policy.
    /// </summary>
    /// <param name="batteryPolicy">Battery conservation policy</param>
    /// <returns>True if battery level is sufficient</returns>
    public async Task<bool> IsBatteryLevelSufficientAsync(BatteryPolicy batteryPolicy)
    {
        try
        {
            var batteryLevel = await GetBatteryLevelAsync();
            var isCharging = Battery.PowerSource != BatteryPowerSource.Unknown;

            // If charging, be more permissive
            if (isCharging)
            {
                return batteryPolicy switch
                {
                    BatteryPolicy.PowerSaver => batteryLevel > 10,
                    BatteryPolicy.Conservative => batteryLevel > 5,
                    BatteryPolicy.Normal => batteryLevel > 3,
                    BatteryPolicy.Performance => batteryLevel > 1,
                    _ => false
                };
            }

            // Battery thresholds when not charging
            return batteryPolicy switch
            {
                BatteryPolicy.PowerSaver => batteryLevel > 50,
                BatteryPolicy.Conservative => batteryLevel > 30,
                BatteryPolicy.Normal => batteryLevel > 15,
                BatteryPolicy.Performance => batteryLevel > 5,
                _ => false
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking battery level");
            return false; // Conservative approach on error
        }
    }

    /// <summary>
    /// Gets the current network connection type.
    /// </summary>
    /// <returns>Network connection type</returns>
    public async Task<NetworkConnectionType> GetNetworkConnectionTypeAsync()
    {
        try
        {
            await Task.CompletedTask; // Make async for consistency

            var profiles = Connectivity.ConnectionProfiles;

            // Check connection types in order of preference
            if (profiles.Contains(ConnectionProfile.WiFi))
                return NetworkConnectionType.WiFi;

            if (profiles.Contains(ConnectionProfile.Cellular))
                return NetworkConnectionType.Cellular;

            if (profiles.Contains(ConnectionProfile.Ethernet))
                return NetworkConnectionType.Ethernet;

            if (profiles.Contains(ConnectionProfile.Bluetooth))
                return NetworkConnectionType.Bluetooth;

            // Check if any connection exists
            if (profiles.Any() && Connectivity.NetworkAccess == NetworkAccess.Internet)
                return NetworkConnectionType.Other;

            return NetworkConnectionType.None;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting network connection type");
            return NetworkConnectionType.None;
        }
    }

    /// <summary>
    /// Gets the current battery level as a percentage.
    /// </summary>
    /// <returns>Battery level (0-100)</returns>
    public async Task<double> GetBatteryLevelAsync()
    {
        try
        {
            await Task.CompletedTask; // Make async for consistency
            return Battery.ChargeLevel * 100.0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting battery level");
            return 100.0; // Conservative assumption
        }
    }

    /// <summary>
    /// Gets the current network speed estimate in bits per second.
    /// Note: This is a basic implementation. Real network speed testing
    /// would require downloading test data or using platform-specific APIs.
    /// </summary>
    /// <returns>Network speed estimate</returns>
    public async Task<long> GetNetworkSpeedAsync()
    {
        try
        {
            var connectionType = await GetNetworkConnectionTypeAsync();

            // Rough estimates based on connection type
            return connectionType switch
            {
                NetworkConnectionType.WiFi => 50_000_000, // 50 Mbps
                NetworkConnectionType.Cellular => 10_000_000, // 10 Mbps (LTE average)
                NetworkConnectionType.Ethernet => 100_000_000, // 100 Mbps
                NetworkConnectionType.Bluetooth => 1_000_000, // 1 Mbps
                NetworkConnectionType.Other => 5_000_000, // 5 Mbps
                _ => 0
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error estimating network speed");
            return 0;
        }
    }

    /// <summary>
    /// Handles connectivity changes from MAUI.Essentials.
    /// </summary>
    private async void OnConnectivityChanged(object? sender, ConnectivityChangedEventArgs e)
    {
        try
        {
            var currentConnectionType = await GetNetworkConnectionTypeAsync();
            var previousConnectionType = _lastConnectionType;
            _lastConnectionType = currentConnectionType;

            var eventArgs = new ConnectivityChangedEventArgs
            {
                PreviousConnectionType = previousConnectionType,
                CurrentConnectionType = currentConnectionType,
                IsConnected = e.NetworkAccess == NetworkAccess.Internet
            };

            _logger.LogDebug("Connectivity changed from {Previous} to {Current}, Connected: {Connected}",
                previousConnectionType, currentConnectionType, eventArgs.IsConnected);

            ConnectivityChanged?.Invoke(this, eventArgs);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling connectivity change");
        }
    }

    /// <summary>
    /// Periodically checks battery level for changes.
    /// In a full implementation, this would use platform-specific event subscriptions.
    /// </summary>
    private async Task CheckBatteryLevelPeriodically()
    {
        try
        {
            var currentBatteryLevel = await GetBatteryLevelAsync();
            var previousBatteryLevel = _lastBatteryLevel;
            _lastBatteryLevel = currentBatteryLevel;

            // Only raise event for significant changes (>= 5%)
            if (Math.Abs(currentBatteryLevel - previousBatteryLevel) >= 5.0)
            {
                var eventArgs = new BatteryChangedEventArgs
                {
                    PreviousBatteryLevel = previousBatteryLevel,
                    CurrentBatteryLevel = currentBatteryLevel,
                    IsCharging = Battery.PowerSource != BatteryPowerSource.Unknown,
                    PowerSource = Battery.PowerSource.ToString()
                };

                _logger.LogDebug("Battery changed from {Previous}% to {Current}%, Charging: {Charging}",
                    previousBatteryLevel, currentBatteryLevel, eventArgs.IsCharging);

                BatteryChanged?.Invoke(this, eventArgs);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling battery change");
        }
    }
}