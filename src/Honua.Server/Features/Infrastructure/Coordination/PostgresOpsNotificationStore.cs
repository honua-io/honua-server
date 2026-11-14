// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Observability.Domain;

namespace Honua.ControlPlane;

internal sealed record OpsNotificationSchedule(
    string Source, string SubjectId, Guid EpisodeId, DateTimeOffset FirstSeenAt,
    OpsFindingSeverity Severity, int NoticeNumber, long Revision);

internal sealed class PostgresOpsNotificationStore
{
    internal PostgresOpsNotificationStore(JobStoreDatabase database) => ArgumentNullException.ThrowIfNull(database);

    public Task ObserveAsync(string source, string subjectId, OpsFindingSeverity severity,
        DateTimeOffset now, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<IReadOnlyList<OpsNotificationSchedule>> ListDueAsync(DateTimeOffset now, int limit,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<OpsNotificationSchedule>>([]);

    public Task<bool> TryAdvanceAsync(OpsNotificationSchedule schedule, DateTimeOffset nextNoticeAt,
        CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task CloseMissingAsync(string source, IReadOnlyList<string> activeSubjects, DateTimeOffset now,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<int> ExpireApprovalsAsync(int limit, CancellationToken cancellationToken = default)
        => Task.FromResult(0);
}
