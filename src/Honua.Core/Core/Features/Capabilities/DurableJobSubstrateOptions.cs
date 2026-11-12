// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Capabilities;

public enum DurableJobSubstrateCause
{
    Available,

    SystemDatabaseNotConfigured,

    RuntimeIncomplete,
}

public sealed class DurableJobSubstrateOptions
{
    public const string SectionName = "DurableJobSubstrate";

    // The job store, job queue and proposal store are tables of the system database, so a host
    // configured with one has the durable job store that Jobs:RequireDurableStore asks for.
    public bool SystemDatabaseConfigured { get; set; }

    public DurableJobSubstrateCause Classify(bool jobStorePresent, bool jobQueuePresent)
    {
        if (jobStorePresent && jobQueuePresent)
        {
            return DurableJobSubstrateCause.Available;
        }

        return SystemDatabaseConfigured
            ? DurableJobSubstrateCause.RuntimeIncomplete
            : DurableJobSubstrateCause.SystemDatabaseNotConfigured;
    }
}
