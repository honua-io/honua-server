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

using Honua.Admin.Sdk.Models;

namespace Honua.Admin.Sdk;

/// <summary>
/// Configuration options for the Honua administrative client.
/// </summary>
public class HonuaAdminClientOptions
{
    /// <summary>
    /// Base URL for the admin API.
    /// </summary>
    public string AdminApiUrl { get; set; } = string.Empty;

    /// <summary>
    /// API key for administrative authentication.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Bearer token for authentication (alternative to API key).
    /// </summary>
    public string? BearerToken { get; set; }

    /// <summary>
    /// Whether to enable real-time updates via SignalR.
    /// </summary>
    public bool EnableRealTimeUpdates { get; set; } = true;

    /// <summary>
    /// Request timeout for administrative operations.
    /// </summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Timeout for bulk operations (longer than regular operations).
    /// </summary>
    public TimeSpan BulkOperationTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Maximum retry attempts for failed requests.
    /// </summary>
    public int MaxRetryAttempts { get; set; } = 3;

    /// <summary>
    /// Base delay between retry attempts.
    /// </summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Default audit logging level.
    /// </summary>
    public AuditLevel DefaultAuditLevel { get; set; } = AuditLevel.Standard;

    /// <summary>
    /// Whether to validate all operations before execution.
    /// </summary>
    public bool ValidateOperationsByDefault { get; set; } = true;

    /// <summary>
    /// Whether to include diagnostic information by default.
    /// </summary>
    public bool IncludeDiagnosticsByDefault { get; set; }

    /// <summary>
    /// Maximum size for bulk import operations (in bytes).
    /// </summary>
    public long MaxBulkImportSize { get; set; } = 100 * 1024 * 1024; // 100MB

    /// <summary>
    /// Default batch size for bulk operations.
    /// </summary>
    public int DefaultBatchSize { get; set; } = 1000;

    /// <summary>
    /// Custom headers to include with all requests.
    /// </summary>
    public Dictionary<string, string> CustomHeaders { get; set; } = new();

    /// <summary>
    /// Whether to compress data for network transfer.
    /// </summary>
    public bool UseCompression { get; set; } = true;

    /// <summary>
    /// SignalR hub URL for real-time updates (if different from base URL).
    /// </summary>
    public string? SignalRHubUrl { get; set; }

    /// <summary>
    /// Connection string for the admin database (for direct access scenarios).
    /// </summary>
    public string? AdminDatabaseConnection { get; set; }
}