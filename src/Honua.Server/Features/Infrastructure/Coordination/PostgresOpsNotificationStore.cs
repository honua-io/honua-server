// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Observability.Domain;

namespace Honua.ControlPlane;

internal sealed record OpsNotificationSchedule(
    string Source, string SubjectId, Guid EpisodeId, DateTimeOffset FirstSeenAt,
    OpsFindingSeverity Severity, int NoticeNumber, long Revision);

internal sealed class PostgresOpsNotificationStore
{
    private readonly JobStoreDatabase _database;

    internal PostgresOpsNotificationStore(JobStoreDatabase database) => _database = database;

    private Task<T> NoOp<T>(T value)
    {
        ArgumentNullException.ThrowIfNull(_database);
        return Task.FromResult(value);
    }

    public Task ObserveAsync(string source, string subjectId, OpsFindingSeverity severity,
        DateTimeOffset now, CancellationToken cancellationToken = default) => NoOp(false);

    public Task<IReadOnlyList<OpsNotificationSchedule>> ListDueAsync(DateTimeOffset now, int limit,
        CancellationToken cancellationToken = default)
        => NoOp<IReadOnlyList<OpsNotificationSchedule>>([]);

    public Task<bool> TryAdvanceAsync(OpsNotificationSchedule schedule, DateTimeOffset nextNoticeAt,
        CancellationToken cancellationToken = default) => NoOp(false);

    public Task CloseMissingAsync(string source, IReadOnlyList<string> activeSubjects, DateTimeOffset now,
        CancellationToken cancellationToken = default) => NoOp(false);

    public Task<int> ExpireApprovalsAsync(int limit, CancellationToken cancellationToken = default)
        => NoOp(0);
}
