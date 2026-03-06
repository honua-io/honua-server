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

using Microsoft.Maui.Essentials;

namespace Honua.Mobile.Sdk.Clients;

/// <summary>
/// Mobile-specific context for feature service operations.
/// Optimized for battery life, offline scenarios, and progress reporting.
/// </summary>
public class MobileContext
{
    /// <summary>
    /// Cancellation token for the operation.
    /// </summary>
    public CancellationToken CancellationToken { get; init; } = default;

    /// <summary>
    /// Progress reporter for long-running operations.
    /// </summary>
    public IProgress<SyncProgress>? ProgressReporter { get; init; }

    /// <summary>
    /// Network access policy for this operation.
    /// </summary>
    public NetworkPolicy NetworkPolicy { get; init; } = NetworkPolicy.PreferWifi;

    /// <summary>
    /// Whether this operation can work offline (use cached data).
    /// </summary>
    public bool AllowOffline { get; init; } = true;

    /// <summary>
    /// Battery conservation mode settings.
    /// </summary>
    public BatteryPolicy BatteryPolicy { get; init; } = BatteryPolicy.Conservative;

    /// <summary>
    /// Custom headers for network requests.
    /// </summary>
    public Dictionary<string, string>? Headers { get; init; }

    /// <summary>
    /// Request timeout (shorter default for mobile).
    /// </summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>
    /// Priority level for the operation.
    /// </summary>
    public OperationPriority Priority { get; init; } = OperationPriority.Normal;

    /// <summary>
    /// Creates a mobile context with progress reporting.
    /// </summary>
    /// <param name="progressReporter">Progress reporter</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Mobile context</returns>
    public static MobileContext WithProgress(IProgress<SyncProgress> progressReporter, CancellationToken cancellationToken = default)
    {
        return new MobileContext
        {
            ProgressReporter = progressReporter,
            CancellationToken = cancellationToken
        };
    }

    /// <summary>
    /// Creates a mobile context for offline-only operations.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Mobile context</returns>
    public static MobileContext OfflineOnly(CancellationToken cancellationToken = default)
    {
        return new MobileContext
        {
            NetworkPolicy = NetworkPolicy.Offline,
            AllowOffline = true,
            CancellationToken = cancellationToken
        };
    }

    /// <summary>
    /// Creates a mobile context for high priority operations.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Mobile context</returns>
    public static MobileContext HighPriority(CancellationToken cancellationToken = default)
    {
        return new MobileContext
        {
            Priority = OperationPriority.High,
            NetworkPolicy = NetworkPolicy.WifiOrCellular,
            BatteryPolicy = BatteryPolicy.Performance,
            CancellationToken = cancellationToken
        };
    }

    /// <summary>
    /// Creates a mobile context optimized for background operations.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Mobile context</returns>
    public static MobileContext Background(CancellationToken cancellationToken = default)
    {
        return new MobileContext
        {
            Priority = OperationPriority.Low,
            NetworkPolicy = NetworkPolicy.PreferWifi,
            BatteryPolicy = BatteryPolicy.Conservative,
            Timeout = TimeSpan.FromMinutes(10), // Longer timeout for background
            CancellationToken = cancellationToken
        };
    }
}

/// <summary>
/// Network access policies for mobile operations.
/// </summary>
public enum NetworkPolicy
{
    /// <summary>
    /// Work offline only, don't access network.
    /// </summary>
    Offline = 0,

    /// <summary>
    /// Prefer WiFi but allow cellular if needed.
    /// </summary>
    PreferWifi = 1,

    /// <summary>
    /// Use WiFi or cellular networks.
    /// </summary>
    WifiOrCellular = 2,

    /// <summary>
    /// Use any available network connection.
    /// </summary>
    Any = 3
}

/// <summary>
/// Battery conservation policies for mobile operations.
/// </summary>
public enum BatteryPolicy
{
    /// <summary>
    /// Maximum battery conservation - minimal processing.
    /// </summary>
    PowerSaver = 0,

    /// <summary>
    /// Conservative battery usage - balanced approach.
    /// </summary>
    Conservative = 1,

    /// <summary>
    /// Normal battery usage.
    /// </summary>
    Normal = 2,

    /// <summary>
    /// Performance over battery life.
    /// </summary>
    Performance = 3
}

/// <summary>
/// Operation priority levels for mobile scenarios.
/// </summary>
public enum OperationPriority
{
    /// <summary>
    /// Low priority - background operations.
    /// </summary>
    Low = 0,

    /// <summary>
    /// Normal priority - user-initiated operations.
    /// </summary>
    Normal = 1,

    /// <summary>
    /// High priority - urgent operations.
    /// </summary>
    High = 2,

    /// <summary>
    /// Critical priority - emergency or safety-related operations.
    /// </summary>
    Critical = 3
}

/// <summary>
/// Progress information for mobile sync operations.
/// </summary>
public class SyncProgress
{
    /// <summary>
    /// Current step in the operation.
    /// </summary>
    public string CurrentStep { get; set; } = string.Empty;

    /// <summary>
    /// Overall progress percentage (0-100).
    /// </summary>
    public double Percentage { get; set; }

    /// <summary>
    /// Number of items processed.
    /// </summary>
    public int ProcessedItems { get; set; }

    /// <summary>
    /// Total number of items to process.
    /// </summary>
    public int TotalItems { get; set; }

    /// <summary>
    /// User-friendly status message.
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Optional error information.
    /// </summary>
    public Exception? Error { get; set; }

    /// <summary>
    /// Whether the operation completed successfully.
    /// </summary>
    public bool IsCompleted { get; set; }

    /// <summary>
    /// Creates progress for a step completion.
    /// </summary>
    /// <param name="step">Step name</param>
    /// <param name="processed">Items processed</param>
    /// <param name="total">Total items</param>
    /// <param name="message">Status message</param>
    /// <returns>Progress object</returns>
    public static SyncProgress Step(string step, int processed, int total, string message)
    {
        return new SyncProgress
        {
            CurrentStep = step,
            ProcessedItems = processed,
            TotalItems = total,
            Percentage = total > 0 ? (double)processed / total * 100 : 0,
            Message = message
        };
    }

    /// <summary>
    /// Creates progress for completion.
    /// </summary>
    /// <param name="message">Completion message</param>
    /// <returns>Progress object</returns>
    public static SyncProgress Completed(string message = "Operation completed successfully")
    {
        return new SyncProgress
        {
            IsCompleted = true,
            Percentage = 100,
            Message = message
        };
    }

    /// <summary>
    /// Creates progress for errors.
    /// </summary>
    /// <param name="error">Error that occurred</param>
    /// <param name="message">Error message</param>
    /// <returns>Progress object</returns>
    public static SyncProgress Error(Exception error, string message)
    {
        return new SyncProgress
        {
            Error = error,
            Message = message,
            IsCompleted = true
        };
    }
}

