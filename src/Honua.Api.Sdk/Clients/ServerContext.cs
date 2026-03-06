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

namespace Honua.Api.Sdk.Clients;

/// <summary>
/// Server-specific context for feature service operations.
/// Optimized for high-performance server-to-server communication with cancellation support.
/// </summary>
public class ServerContext
{
    /// <summary>
    /// Cancellation token for the operation.
    /// </summary>
    public CancellationToken CancellationToken { get; init; } = default;

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
    /// Whether to bypass client-side caching for this request.
    /// </summary>
    public bool BypassCache { get; init; }

    /// <summary>
    /// Priority level for request scheduling.
    /// </summary>
    public RequestPriority Priority { get; init; } = RequestPriority.Normal;

    /// <summary>
    /// Creates a server context with just a cancellation token.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Server context</returns>
    public static ServerContext WithCancellation(CancellationToken cancellationToken)
    {
        return new ServerContext { CancellationToken = cancellationToken };
    }

    /// <summary>
    /// Creates a server context with custom headers.
    /// </summary>
    /// <param name="headers">Custom headers</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Server context</returns>
    public static ServerContext WithHeaders(Dictionary<string, string> headers, CancellationToken cancellationToken = default)
    {
        return new ServerContext
        {
            Headers = headers,
            CancellationToken = cancellationToken
        };
    }

    /// <summary>
    /// Creates a server context for high priority requests.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Server context</returns>
    public static ServerContext HighPriority(CancellationToken cancellationToken = default)
    {
        return new ServerContext
        {
            Priority = RequestPriority.High,
            CancellationToken = cancellationToken
        };
    }
}

/// <summary>
/// Request priority levels for server operations.
/// </summary>
public enum RequestPriority
{
    /// <summary>
    /// Low priority - can be throttled or delayed.
    /// </summary>
    Low = 0,

    /// <summary>
    /// Normal priority - default level.
    /// </summary>
    Normal = 1,

    /// <summary>
    /// High priority - processed with minimal delay.
    /// </summary>
    High = 2,

    /// <summary>
    /// Critical priority - for emergency or system operations.
    /// </summary>
    Critical = 3
}