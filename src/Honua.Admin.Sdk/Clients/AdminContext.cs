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

using System.Diagnostics;

namespace Honua.Admin.Sdk.Clients;

/// <summary>
/// Administrative context for Honua admin operations.
/// Optimized for administrative tasks, bulk operations, and detailed diagnostics.
/// </summary>
public class AdminContext
{
    /// <summary>
    /// Cancellation token for the operation.
    /// </summary>
    public CancellationToken CancellationToken { get; init; } = default;

    /// <summary>
    /// User identity for audit logging.
    /// </summary>
    public AdminIdentity? UserIdentity { get; init; }

    /// <summary>
    /// Progress reporter for long-running admin operations.
    /// </summary>
    public IProgress<AdminProgress>? ProgressReporter { get; init; }

    /// <summary>
    /// Optional activity for distributed tracing.
    /// </summary>
    public Activity? Activity { get; init; }

    /// <summary>
    /// Custom headers for the request.
    /// </summary>
    public Dictionary<string, string>? Headers { get; init; }

    /// <summary>
    /// Request timeout override (uses client default if null).
    /// </summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>
    /// Whether to include detailed diagnostic information.
    /// </summary>
    public bool IncludeDiagnostics { get; init; }

    /// <summary>
    /// Whether to validate operations before execution.
    /// </summary>
    public bool ValidateOperations { get; init; } = true;

    /// <summary>
    /// Audit logging level for this operation.
    /// </summary>
    public AuditLevel AuditLevel { get; init; } = AuditLevel.Standard;

    /// <summary>
    /// Priority level for admin operations.
    /// </summary>
    public AdminPriority Priority { get; init; } = AdminPriority.Normal;

    /// <summary>
    /// Creates an admin context with user identity.
    /// </summary>
    /// <param name="userId">User ID performing the operation</param>
    /// <param name="userEmail">User email for audit trail</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Admin context</returns>
    public static AdminContext ForUser(string userId, string userEmail, CancellationToken cancellationToken = default)
    {
        return new AdminContext
        {
            UserIdentity = new AdminIdentity
            {
                UserId = userId,
                Email = userEmail,
                Timestamp = DateTime.UtcNow
            },
            CancellationToken = cancellationToken
        };
    }

    /// <summary>
    /// Creates an admin context for bulk operations with progress reporting.
    /// </summary>
    /// <param name="progressReporter">Progress reporter</param>
    /// <param name="userIdentity">User performing the bulk operation</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Admin context</returns>
    public static AdminContext ForBulkOperation(
        IProgress<AdminProgress> progressReporter,
        AdminIdentity userIdentity,
        CancellationToken cancellationToken = default)
    {
        return new AdminContext
        {
            ProgressReporter = progressReporter,
            UserIdentity = userIdentity,
            AuditLevel = AuditLevel.Detailed,
            ValidateOperations = true,
            CancellationToken = cancellationToken
        };
    }

    /// <summary>
    /// Creates an admin context for system operations.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Admin context</returns>
    public static AdminContext System(CancellationToken cancellationToken = default)
    {
        return new AdminContext
        {
            UserIdentity = new AdminIdentity
            {
                UserId = "system",
                Email = "system@honua",
                Timestamp = DateTime.UtcNow
            },
            Priority = AdminPriority.System,
            AuditLevel = AuditLevel.Minimal,
            CancellationToken = cancellationToken
        };
    }

    /// <summary>
    /// Creates an admin context for high-priority operations.
    /// </summary>
    /// <param name="userIdentity">User performing the operation</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Admin context</returns>
    public static AdminContext HighPriority(AdminIdentity userIdentity, CancellationToken cancellationToken = default)
    {
        return new AdminContext
        {
            UserIdentity = userIdentity,
            Priority = AdminPriority.High,
            IncludeDiagnostics = true,
            AuditLevel = AuditLevel.Detailed,
            CancellationToken = cancellationToken
        };
    }
}

/// <summary>
/// Identity information for administrative operations.
/// </summary>
public class AdminIdentity
{
    /// <summary>
    /// User ID performing the operation.
    /// </summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>
    /// User email address.
    /// </summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>
    /// Optional display name.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// User roles for authorization.
    /// </summary>
    public IList<string> Roles { get; set; } = new List<string>();

    /// <summary>
    /// Timestamp when the identity was created.
    /// </summary>
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// Session ID for tracking.
    /// </summary>
    public string? SessionId { get; set; }

    /// <summary>
    /// IP address of the user.
    /// </summary>
    public string? IpAddress { get; set; }

    /// <summary>
    /// User agent string.
    /// </summary>
    public string? UserAgent { get; set; }
}

/// <summary>
/// Progress information for administrative operations.
/// </summary>
public class AdminProgress
{
    /// <summary>
    /// Current operation being performed.
    /// </summary>
    public string Operation { get; set; } = string.Empty;

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
    /// Number of warnings encountered.
    /// </summary>
    public int WarningCount { get; set; }

    /// <summary>
    /// Number of errors encountered.
    /// </summary>
    public int ErrorCount { get; set; }

    /// <summary>
    /// Whether the operation completed successfully.
    /// </summary>
    public bool IsCompleted { get; set; }

    /// <summary>
    /// Detailed diagnostic information if enabled.
    /// </summary>
    public AdminDiagnostics? Diagnostics { get; set; }

    /// <summary>
    /// Creates progress for an operation step.
    /// </summary>
    /// <param name="operation">Operation name</param>
    /// <param name="step">Current step</param>
    /// <param name="processed">Items processed</param>
    /// <param name="total">Total items</param>
    /// <param name="message">Status message</param>
    /// <returns>Admin progress</returns>
    public static AdminProgress Step(string operation, string step, int processed, int total, string message)
    {
        return new AdminProgress
        {
            Operation = operation,
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
    /// <param name="operation">Operation name</param>
    /// <param name="message">Completion message</param>
    /// <param name="warnings">Number of warnings</param>
    /// <param name="errors">Number of errors</param>
    /// <returns>Admin progress</returns>
    public static AdminProgress Completed(string operation, string message, int warnings = 0, int errors = 0)
    {
        return new AdminProgress
        {
            Operation = operation,
            IsCompleted = true,
            Percentage = 100,
            Message = message,
            WarningCount = warnings,
            ErrorCount = errors
        };
    }
}

/// <summary>
/// Diagnostic information for administrative operations.
/// </summary>
public class AdminDiagnostics
{
    /// <summary>
    /// Memory usage during the operation.
    /// </summary>
    public long MemoryUsageBytes { get; set; }

    /// <summary>
    /// CPU usage percentage.
    /// </summary>
    public double CpuUsagePercent { get; set; }

    /// <summary>
    /// Network I/O statistics.
    /// </summary>
    public NetworkStatistics? NetworkStats { get; set; }

    /// <summary>
    /// Database performance metrics.
    /// </summary>
    public DatabaseStatistics? DatabaseStats { get; set; }

    /// <summary>
    /// Custom performance counters.
    /// </summary>
    public Dictionary<string, object> CustomMetrics { get; set; } = new();
}

/// <summary>
/// Network I/O statistics.
/// </summary>
public class NetworkStatistics
{
    /// <summary>
    /// Bytes sent over the network.
    /// </summary>
    public long BytesSent { get; set; }

    /// <summary>
    /// Bytes received from the network.
    /// </summary>
    public long BytesReceived { get; set; }

    /// <summary>
    /// Number of network requests.
    /// </summary>
    public int RequestCount { get; set; }

    /// <summary>
    /// Average network latency.
    /// </summary>
    public TimeSpan AverageLatency { get; set; }
}

/// <summary>
/// Database performance statistics.
/// </summary>
public class DatabaseStatistics
{
    /// <summary>
    /// Number of database queries executed.
    /// </summary>
    public int QueryCount { get; set; }

    /// <summary>
    /// Total time spent on database operations.
    /// </summary>
    public TimeSpan TotalQueryTime { get; set; }

    /// <summary>
    /// Average query execution time.
    /// </summary>
    public TimeSpan AverageQueryTime { get; set; }

    /// <summary>
    /// Number of rows affected.
    /// </summary>
    public int RowsAffected { get; set; }
}

/// <summary>
/// Audit logging levels for admin operations.
/// </summary>
public enum AuditLevel
{
    /// <summary>
    /// Minimal audit logging - operation start/end only.
    /// </summary>
    Minimal = 0,

    /// <summary>
    /// Standard audit logging - includes key parameters and results.
    /// </summary>
    Standard = 1,

    /// <summary>
    /// Detailed audit logging - includes all parameters and diagnostics.
    /// </summary>
    Detailed = 2,

    /// <summary>
    /// Full audit logging - includes all data for compliance.
    /// </summary>
    Full = 3
}

/// <summary>
/// Priority levels for administrative operations.
/// </summary>
public enum AdminPriority
{
    /// <summary>
    /// Low priority - background maintenance operations.
    /// </summary>
    Low = 0,

    /// <summary>
    /// Normal priority - standard administrative tasks.
    /// </summary>
    Normal = 1,

    /// <summary>
    /// High priority - urgent administrative operations.
    /// </summary>
    High = 2,

    /// <summary>
    /// System priority - critical system operations.
    /// </summary>
    System = 3
}