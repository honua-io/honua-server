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
    private readonly string _instanceId;
    private readonly object _lock = new();
    private readonly Queue<RecentErrorEntry> _entries = new();

    public RecentErrorBuffer(IOptions<RecentErrorBufferOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _capacity = Math.Max(0, options.Value.Capacity);
        _instanceId = $"{Environment.MachineName}-{Environment.ProcessId}";
    }

    /// <summary>
    /// Maximum number of errors retained.
    /// </summary>
    public int Capacity => _capacity;

    /// <summary>
    /// Identifier for the current node that owns this buffer.
    /// </summary>
    public string InstanceId => _instanceId;

    /// <summary>
    /// Record a server error response into the buffer.
    /// </summary>
    public void Record(HttpContext context, StandardErrorResponse errorResponse)
        => Record(context, errorResponse.StatusCode, errorResponse.Title, errorResponse.Detail);

    /// <summary>
    /// Record an error response into the buffer.
    /// </summary>
    /// <param name="context">Current request context.</param>
    /// <param name="statusCode">HTTP status code.</param>
    /// <param name="title">Short error title.</param>
    /// <param name="detail">Detailed error text.</param>
    /// <param name="includeClientErrors">Whether to include 4xx responses.</param>
    public void Record(
        HttpContext context,
        int statusCode,
        string title,
        string detail,
        bool includeClientErrors = false)
    {
        var minimumStatusCode = includeClientErrors
            ? StatusCodes.Status400BadRequest
            : StatusCodes.Status500InternalServerError;
        if (_capacity <= 0 || statusCode < minimumStatusCode)
        {
            return;
        }

        var message = RecentErrorSanitizer.Sanitize(detail);
        if (string.IsNullOrWhiteSpace(message))
        {
            message = RecentErrorSanitizer.Sanitize(title);
        }

        var entry = new RecentErrorEntry
        {
            Timestamp = DateTimeOffset.UtcNow,
            CorrelationId = string.IsNullOrWhiteSpace(context.TraceIdentifier) ? "unknown" : context.TraceIdentifier,
            Path = context.Request.Path.HasValue ? context.Request.Path.Value! : string.Empty,
            StatusCode = statusCode,
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
    /// Inserts a deterministic fixture entry without requiring an HTTP failure path.
    /// Existing entries with the same correlation, path, status, and message are replaced.
    /// </summary>
    public void RecordSeed(RecentErrorEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (_capacity <= 0)
        {
            return;
        }

        lock (_lock)
        {
            var retained = _entries
                .Where(existing => !IsSameSeedEntry(existing, entry))
                .TakeLast(Math.Max(0, _capacity - 1))
                .ToArray();

            _entries.Clear();
            foreach (var retainedEntry in retained)
            {
                _entries.Enqueue(retainedEntry);
            }

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

    private static bool IsSameSeedEntry(RecentErrorEntry left, RecentErrorEntry right)
        => string.Equals(left.CorrelationId, right.CorrelationId, StringComparison.Ordinal) &&
           string.Equals(left.Path, right.Path, StringComparison.Ordinal) &&
           left.StatusCode == right.StatusCode &&
           string.Equals(left.Message, right.Message, StringComparison.Ordinal);
}
