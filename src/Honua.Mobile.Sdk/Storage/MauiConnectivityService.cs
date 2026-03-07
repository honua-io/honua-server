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

using System.Net.NetworkInformation;
using Microsoft.Extensions.Logging;
using Honua.Mobile.Sdk.Clients;

namespace Honua.Mobile.Sdk.Storage;

/// <summary>
/// Connectivity service used by the mobile SDK.
/// In the non-platform build, this falls back to generic .NET networking primitives.
/// </summary>
public class MauiConnectivityService : IConnectivityService
{
    private readonly ILogger<MauiConnectivityService> _logger;

    /// <summary>
    /// Initializes a new instance of the MauiConnectivityService.
    /// </summary>
    /// <param name="logger">Logger instance</param>
    public MauiConnectivityService(ILogger<MauiConnectivityService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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
    public async Task<bool> IsConnectionAvailableAsync(NetworkPolicy networkPolicy)
    {
        if (networkPolicy == NetworkPolicy.Offline)
        {
            return false;
        }

        var connectionType = await GetNetworkConnectionTypeAsync();
        var isAvailable = connectionType != NetworkConnectionType.None;

        if (!isAvailable)
        {
            _logger.LogDebug("No network interfaces with connectivity are currently available");
            return false;
        }

        return networkPolicy switch
        {
            NetworkPolicy.PreferWifi => connectionType is NetworkConnectionType.WiFi or NetworkConnectionType.Ethernet,
            NetworkPolicy.WifiOrCellular => connectionType is NetworkConnectionType.WiFi or NetworkConnectionType.Ethernet or NetworkConnectionType.Cellular,
            NetworkPolicy.Any => true,
            _ => false
        };
    }

    /// <summary>
    /// Checks if the battery level is sufficient for the specified policy.
    /// </summary>
    public Task<bool> IsBatteryLevelSufficientAsync(BatteryPolicy batteryPolicy)
    {
        _ = batteryPolicy;
        return Task.FromResult(true);
    }

    /// <summary>
    /// Gets the current network connection type.
    /// </summary>
    public Task<NetworkConnectionType> GetNetworkConnectionTypeAsync()
    {
        if (!NetworkInterface.GetIsNetworkAvailable())
        {
            RaiseConnectivityChanged(NetworkConnectionType.None, false);
            return Task.FromResult(NetworkConnectionType.None);
        }

        var activeInterfaces = NetworkInterface.GetAllNetworkInterfaces()
            .Where(ni => ni.OperationalStatus == OperationalStatus.Up)
            .Select(MapConnectionType)
            .ToArray();

        var connectionType = SelectPreferredConnectionType(activeInterfaces);
        RaiseConnectivityChanged(connectionType, connectionType != NetworkConnectionType.None);
        return Task.FromResult(connectionType);
    }

    /// <summary>
    /// Gets the current battery level as a percentage.
    /// </summary>
    public Task<double> GetBatteryLevelAsync()
    {
        RaiseBatteryChanged(100.0, false, "Unknown");
        return Task.FromResult(100.0);
    }

    /// <summary>
    /// Gets the current network speed estimate in bits per second.
    /// </summary>
    public async Task<long> GetNetworkSpeedAsync()
    {
        var connectionType = await GetNetworkConnectionTypeAsync();

        return connectionType switch
        {
            NetworkConnectionType.WiFi => 50_000_000,
            NetworkConnectionType.Cellular => 10_000_000,
            NetworkConnectionType.Ethernet => 100_000_000,
            NetworkConnectionType.Bluetooth => 1_000_000,
            NetworkConnectionType.Other => 5_000_000,
            _ => 0,
        };
    }

    private static NetworkConnectionType MapConnectionType(NetworkInterface networkInterface)
    {
        return networkInterface.NetworkInterfaceType switch
        {
            NetworkInterfaceType.Wireless80211 => NetworkConnectionType.WiFi,
            NetworkInterfaceType.Ppp => NetworkConnectionType.Cellular,
            NetworkInterfaceType.Wwanpp => NetworkConnectionType.Cellular,
            NetworkInterfaceType.Wwanpp2 => NetworkConnectionType.Cellular,
            NetworkInterfaceType.Ethernet => NetworkConnectionType.Ethernet,
            NetworkInterfaceType.GigabitEthernet => NetworkConnectionType.Ethernet,
            NetworkInterfaceType.FastEthernetFx => NetworkConnectionType.Ethernet,
            NetworkInterfaceType.FastEthernetT => NetworkConnectionType.Ethernet,
            _ => NetworkConnectionType.Other,
        };
    }

    private static NetworkConnectionType SelectPreferredConnectionType(IEnumerable<NetworkConnectionType> connectionTypes)
    {
        if (connectionTypes.Contains(NetworkConnectionType.WiFi))
        {
            return NetworkConnectionType.WiFi;
        }

        if (connectionTypes.Contains(NetworkConnectionType.Ethernet))
        {
            return NetworkConnectionType.Ethernet;
        }

        if (connectionTypes.Contains(NetworkConnectionType.Cellular))
        {
            return NetworkConnectionType.Cellular;
        }

        if (connectionTypes.Contains(NetworkConnectionType.Bluetooth))
        {
            return NetworkConnectionType.Bluetooth;
        }

        return connectionTypes.FirstOrDefault(NetworkConnectionType.None);
    }

    private void RaiseConnectivityChanged(NetworkConnectionType currentConnectionType, bool isConnected)
    {
        var handler = ConnectivityChanged;
        if (handler is null)
        {
            return;
        }

        handler(this, new ConnectivityChangedEventArgs
        {
            PreviousConnectionType = currentConnectionType,
            CurrentConnectionType = currentConnectionType,
            IsConnected = isConnected,
        });
    }

    private void RaiseBatteryChanged(double currentBatteryLevel, bool isCharging, string powerSource)
    {
        var handler = BatteryChanged;
        if (handler is null)
        {
            return;
        }

        handler(this, new BatteryChangedEventArgs
        {
            PreviousBatteryLevel = currentBatteryLevel,
            CurrentBatteryLevel = currentBatteryLevel,
            IsCharging = isCharging,
            PowerSource = powerSource,
        });
    }
}
