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

using Honua.Mobile.Sdk.Clients;

namespace Honua.Mobile.Sdk.Storage;

/// <summary>
/// Interface for monitoring network connectivity and battery status in mobile environments.
/// </summary>
public interface IConnectivityService
{
    /// <summary>
    /// Checks if a network connection is available based on the specified policy.
    /// </summary>
    /// <param name="networkPolicy">Network access policy</param>
    /// <returns>True if connection is available</returns>
    Task<bool> IsConnectionAvailableAsync(NetworkPolicy networkPolicy);

    /// <summary>
    /// Checks if the battery level is sufficient for the specified policy.
    /// </summary>
    /// <param name="batteryPolicy">Battery conservation policy</param>
    /// <returns>True if battery level is sufficient</returns>
    Task<bool> IsBatteryLevelSufficientAsync(BatteryPolicy batteryPolicy);

    /// <summary>
    /// Gets the current network connection type.
    /// </summary>
    /// <returns>Network connection type</returns>
    Task<NetworkConnectionType> GetNetworkConnectionTypeAsync();

    /// <summary>
    /// Gets the current battery level as a percentage.
    /// </summary>
    /// <returns>Battery level (0-100)</returns>
    Task<double> GetBatteryLevelAsync();

    /// <summary>
    /// Gets the current network speed estimate in bits per second.
    /// </summary>
    /// <returns>Network speed estimate</returns>
    Task<long> GetNetworkSpeedAsync();

    /// <summary>
    /// Event fired when connectivity status changes.
    /// </summary>
    event EventHandler<ConnectivityChangedEventArgs> ConnectivityChanged;

    /// <summary>
    /// Event fired when battery status changes significantly.
    /// </summary>
    event EventHandler<BatteryChangedEventArgs> BatteryChanged;
}

/// <summary>
/// Types of network connections.
/// </summary>
public enum NetworkConnectionType
{
    /// <summary>
    /// No network connection.
    /// </summary>
    None,

    /// <summary>
    /// WiFi connection.
    /// </summary>
    WiFi,

    /// <summary>
    /// Cellular data connection.
    /// </summary>
    Cellular,

    /// <summary>
    /// Ethernet connection (Windows/desktop).
    /// </summary>
    Ethernet,

    /// <summary>
    /// Bluetooth connection.
    /// </summary>
    Bluetooth,

    /// <summary>
    /// Other/unknown connection type.
    /// </summary>
    Other
}

/// <summary>
/// Event arguments for connectivity changes.
/// </summary>
public class ConnectivityChangedEventArgs : EventArgs
{
    /// <summary>
    /// Previous network connection type.
    /// </summary>
    public NetworkConnectionType PreviousConnectionType { get; set; }

    /// <summary>
    /// Current network connection type.
    /// </summary>
    public NetworkConnectionType CurrentConnectionType { get; set; }

    /// <summary>
    /// Whether network is currently available.
    /// </summary>
    public bool IsConnected { get; set; }
}

/// <summary>
/// Event arguments for battery changes.
/// </summary>
public class BatteryChangedEventArgs : EventArgs
{
    /// <summary>
    /// Previous battery level percentage.
    /// </summary>
    public double PreviousBatteryLevel { get; set; }

    /// <summary>
    /// Current battery level percentage.
    /// </summary>
    public double CurrentBatteryLevel { get; set; }

    /// <summary>
    /// Whether the device is currently charging.
    /// </summary>
    public bool IsCharging { get; set; }

    /// <summary>
    /// Power source (e.g., AC, USB, Wireless).
    /// </summary>
    public string PowerSource { get; set; } = string.Empty;
}