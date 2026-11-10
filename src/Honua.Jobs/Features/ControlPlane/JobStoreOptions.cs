// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;

namespace Honua.ControlPlane;

internal sealed class JobStoreOptions
{
    public const string SectionName = "Jobs:Store";

    /// <summary>How long a job is kept after its last status or record change once it is terminal.</summary>
    [Range(typeof(TimeSpan), "00:01:00", "365.00:00:00")]
    public TimeSpan JobRetention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>How long a job's log is kept from its first entry unless a caller sets another period.</summary>
    [Range(typeof(TimeSpan), "00:01:00", "365.00:00:00")]
    public TimeSpan LogRetention { get; set; } = TimeSpan.FromDays(7);
}
