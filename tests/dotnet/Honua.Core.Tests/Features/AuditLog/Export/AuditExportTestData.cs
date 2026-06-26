// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.AuditLog.Export;

namespace Honua.Core.Tests.Features.AuditLog.Export;

/// <summary>
/// Shared fixtures for the audit export unit tests (#2157).
/// </summary>
internal static class AuditExportTestData
{
    public static AuditEvent SampleEvent(
        DateTimeOffset? timestamp = null,
        string action = "layer.delete",
        AuditOutcome outcome = AuditOutcome.Denied)
        => new()
        {
            Timestamp = timestamp ?? new DateTimeOffset(2026, 5, 20, 12, 0, 0, TimeSpan.Zero),
            EventType = AuditEventType.Authorization,
            Actor = "user-7",
            ActorType = AuditActorType.UserId,
            ResourceType = "layer",
            ResourceId = "roads",
            Action = action,
            Outcome = outcome,
            CorrelationId = "corr-xyz",
            RemoteIp = "10.0.0.9",
            UserAgent = "honua-test/1.0",
            Details = "{\"status\":403}",
        };

    public static IReadOnlyList<AuditEvent> Batch(int count = 2)
    {
        var list = new List<AuditEvent>(count);
        for (var i = 0; i < count; i++)
        {
            list.Add(SampleEvent(action: $"action.{i}"));
        }

        return list;
    }
}

/// <summary>
/// Test double for <see cref="IAuditSink"/> that returns a scripted sequence of
/// results and counts invocations.
/// </summary>
internal sealed class FakeAuditSink : IAuditSink
{
    private readonly Queue<AuditSinkResult> _scripted;
    private AuditSinkResult _last;

    public FakeAuditSink(string? region, params AuditSinkResult[] results)
    {
        Region = region;
        _scripted = new Queue<AuditSinkResult>(results);
        _last = results.Length > 0 ? results[^1] : AuditSinkResult.Success();
    }

    public string SinkType => "fake";

    public string? Region { get; }

    public int CallCount { get; private set; }

    public Task<AuditSinkResult> SendAsync(IReadOnlyList<AuditEvent> events, CancellationToken ct)
    {
        CallCount++;
        if (_scripted.Count > 0)
        {
            _last = _scripted.Dequeue();
        }

        return Task.FromResult(_last);
    }
}
