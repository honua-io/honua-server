// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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
    public CancellationToken CancellationToken { get; init; }

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
