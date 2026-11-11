// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.ControlPlane;

internal sealed class JobStoreOptions
{
    public const string SectionName = "Jobs:Store";

    // How long a job is kept after its last status or record change once it is terminal.
    public TimeSpan JobRetention { get; set; } = TimeSpan.FromDays(7);

    // How long a job's log is kept from its first entry unless a caller sets another period.
    public TimeSpan LogRetention { get; set; } = TimeSpan.FromDays(7);

    public int WorkerMaxPoolSize { get; set; } = 4;

    public TimeSpan RetentionSweepInterval { get; set; } = TimeSpan.FromMinutes(10);

    public int RetentionSweepBatchSize { get; set; } = 1000;
}
