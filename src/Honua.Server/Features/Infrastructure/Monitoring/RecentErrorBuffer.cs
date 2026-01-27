// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.Infrastructure.Models;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Infrastructure.Monitoring;

/// <summary>
/// In-memory ring buffer for recent errors.
/// </summary>
internal sealed class RecentErrorBuffer
{
    private readonly int _capacity;
    private readonly object _lock = new();
    private readonly Queue<RecentErrorEntry> _entries = new();

    public RecentErrorBuffer(IOptions<RecentErrorBufferOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _capacity = Math.Max(0, options.Value.Capacity);
    }

    /// <summary>
    /// Maximum number of errors retained.
    /// </summary>
    public int Capacity => _capacity;

    /// <summary>
    /// Record a server error response into the buffer.
    /// </summary>
    public void Record(HttpContext context, StandardErrorResponse errorResponse)
    {
        if (_capacity <= 0 || errorResponse.StatusCode < StatusCodes.Status500InternalServerError)
        {
            return;
        }

        var message = RecentErrorSanitizer.Sanitize(errorResponse.Detail);
        if (string.IsNullOrWhiteSpace(message))
        {
            message = RecentErrorSanitizer.Sanitize(errorResponse.Title);
        }

        var entry = new RecentErrorEntry
        {
            Timestamp = DateTimeOffset.UtcNow,
            CorrelationId = string.IsNullOrWhiteSpace(context.TraceIdentifier) ? "unknown" : context.TraceIdentifier,
            Path = context.Request.Path.HasValue ? context.Request.Path.Value! : string.Empty,
            StatusCode = errorResponse.StatusCode,
            Message = message
        };

        lock (_lock)
        {
            while (_entries.Count >= _capacity)
            {
                _entries.Dequeue();
            }

            _entries.Enqueue(entry);
        }
    }

    /// <summary>
    /// Returns recent errors ordered newest-first.
    /// </summary>
    public IReadOnlyList<RecentErrorEntry> Snapshot()
    {
        lock (_lock)
        {
            return _entries.Reverse().ToArray();
        }
    }
}
