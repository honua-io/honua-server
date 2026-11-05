// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Infrastructure.Models;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Monitoring;

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

    public int Capacity => _capacity;

    public string InstanceId => _instanceId;

    public void Record(HttpContext context, StandardErrorResponse errorResponse)
        => Record(context, errorResponse.StatusCode, errorResponse.Title, errorResponse.Detail);

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
