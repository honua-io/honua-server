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

namespace Honua.Api.Sdk;

/// <summary>
/// Configuration options for the Honua API client.
/// </summary>
public class HonuaApiClientOptions
{
    /// <summary>
    /// Base address of the Honua API server.
    /// </summary>
    public string BaseAddress { get; set; } = string.Empty;

    /// <summary>
    /// API key for authentication.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Bearer token for authentication (alternative to API key).
    /// </summary>
    public string? BearerToken { get; set; }

    /// <summary>
    /// Whether to use connection pooling for improved performance.
    /// </summary>
    public bool UseConnectionPooling { get; set; } = true;

    /// <summary>
    /// Maximum number of connections per server when pooling is enabled.
    /// </summary>
    public int MaxConnectionsPerServer { get; set; } = 100;

    /// <summary>
    /// How long a connection can be kept alive.
    /// </summary>
    public TimeSpan ConnectionLifetime { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Maximum number of retry attempts for failed requests.
    /// </summary>
    public int MaxRetryAttempts { get; set; } = 3;

    /// <summary>
    /// Base delay between retry attempts.
    /// </summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Timeout for individual HTTP requests.
    /// </summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Default page size for streaming query results.
    /// </summary>
    public int StreamingPageSize { get; set; } = 1000;

    /// <summary>
    /// Timeout for streaming operations.
    /// </summary>
    public TimeSpan StreamingTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Whether to prefer gRPC over REST when both are available.
    /// </summary>
    public bool PreferGrpc { get; set; } = true;

    /// <summary>
    /// Custom headers to include with all requests.
    /// </summary>
    public Dictionary<string, string> CustomHeaders { get; set; } = new();
}