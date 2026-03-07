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

namespace Honua.Mobile.Sdk;

/// <summary>
/// Configuration options for the Honua mobile client.
/// </summary>
public class HonuaMobileClientOptions
{
    /// <summary>
    /// Base address of the Honua server.
    /// </summary>
    public string ServerAddress { get; set; } = string.Empty;

    /// <summary>
    /// API key for authentication.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Bearer token for authentication (alternative to API key).
    /// </summary>
    public string? BearerToken { get; set; }

    /// <summary>
    /// Path to the offline GeoPackage database file.
    /// </summary>
    public string OfflineDatabase { get; set; } = "honua_offline.gpkg";

    /// <summary>
    /// Backward-compatible switch for consumers that explicitly enable offline mode.
    /// </summary>
    public bool EnableOfflineMode { get; set; } = true;

    /// <summary>
    /// Maximum number of features to store offline.
    /// </summary>
    public int OfflineMaxFeatures { get; set; } = 50000;

    /// <summary>
    /// Number of days to retain offline data before cleanup.
    /// </summary>
    public int OfflineRetentionDays { get; set; } = 30;

    /// <summary>
    /// Whether to automatically cleanup old offline data.
    /// </summary>
    public bool AutoCleanup { get; set; } = true;

    /// <summary>
    /// Default sync policy for network operations.
    /// </summary>
    public SyncPolicy SyncPolicy { get; set; } = SyncPolicy.WifiOnly;

    /// <summary>
    /// Request timeout for mobile operations (shorter than server).
    /// </summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Page size for mobile streaming operations.
    /// </summary>
    public int MobilePageSize { get; set; } = 500;

    /// <summary>
    /// Timeout for streaming operations.
    /// </summary>
    public TimeSpan StreamingTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Background sync interval.
    /// </summary>
    public TimeSpan BackgroundSyncInterval { get; set; } = TimeSpan.FromHours(4);

    /// <summary>
    /// Whether to enable background sync.
    /// </summary>
    public bool EnableBackgroundSync { get; set; } = true;

    /// <summary>
    /// Whether to compress data for network transfer.
    /// </summary>
    public bool UseCompression { get; set; } = true;

    /// <summary>
    /// Custom headers to include with requests.
    /// </summary>
    public Dictionary<string, string> CustomHeaders { get; set; } = new();

    /// <summary>
    /// GPS accuracy threshold in meters.
    /// </summary>
    public double GpsAccuracyThreshold { get; set; } = 10.0;

    /// <summary>
    /// Whether to enable location tracking.
    /// </summary>
    public bool EnableLocationTracking { get; set; } = true;

    /// <summary>
    /// Maximum photo attachment size in bytes.
    /// </summary>
    public long MaxPhotoSize { get; set; } = 5 * 1024 * 1024; // 5MB
}

/// <summary>
/// Sync policies for mobile operations.
/// </summary>
public enum SyncPolicy
{
    /// <summary>
    /// Only sync over WiFi connections.
    /// </summary>
    WifiOnly,

    /// <summary>
    /// Sync over WiFi or cellular connections.
    /// </summary>
    WifiOrCellular,

    /// <summary>
    /// Manual sync only - never automatically sync.
    /// </summary>
    Manual
}
