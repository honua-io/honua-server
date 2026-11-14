// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;

namespace Honua.ControlPlane;

// How long a record is kept after its last write once it is terminal. An active record is never deleted.
internal sealed class ControlPlaneRetentionOptions
{
    public const string SectionName = "ControlPlane:Retention";

    private const string ShortestRetention = "00:01:00";
    private const string LongestRetention = "3650.00:00:00";

    [Range(typeof(TimeSpan), ShortestRetention, LongestRetention)]
    public TimeSpan Proposals { get; set; } = TimeSpan.FromDays(365);

    [Range(typeof(TimeSpan), ShortestRetention, LongestRetention)]
    public TimeSpan OperationInstances { get; set; } = TimeSpan.FromDays(30);

    [Range(typeof(TimeSpan), ShortestRetention, LongestRetention)]
    public TimeSpan WorkflowOperations { get; set; } = TimeSpan.FromDays(7);

    [Range(typeof(TimeSpan), ShortestRetention, LongestRetention)]
    public TimeSpan WorkflowRuns { get; set; } = TimeSpan.FromDays(7);
}
