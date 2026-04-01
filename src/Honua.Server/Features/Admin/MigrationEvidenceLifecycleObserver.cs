// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Admin;

internal interface IMigrationEvidenceLifecycleObserver
{
    Task OnReportPersistedAsync(string jobId, Guid reportId, CancellationToken cancellationToken);
}

internal sealed class NoOpMigrationEvidenceLifecycleObserver : IMigrationEvidenceLifecycleObserver
{
    public Task OnReportPersistedAsync(string jobId, Guid reportId, CancellationToken cancellationToken)
        => Task.CompletedTask;
}
