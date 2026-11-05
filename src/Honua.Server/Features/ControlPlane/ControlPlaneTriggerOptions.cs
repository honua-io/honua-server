// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.ControlPlane;

internal enum ControlPlaneTriggerMode
{
    Poll,

    Event
}

internal sealed class ControlPlaneTriggerOptions
{
    public const string SectionName = "ControlPlane";

    public ControlPlaneTriggerMode TriggerMode { get; set; } = ControlPlaneTriggerMode.Poll;

    public TimeSpan BackstopInterval { get; set; } = TimeSpan.FromMinutes(2);

    // The backstop skips operations an event already advanced and reconciles only ones that look stuck.
    public TimeSpan StaleThreshold { get; set; } = TimeSpan.FromSeconds(90);

    // An empty scheduled-tick token makes the endpoint refuse every request, so an unconfigured deployment cannot be driven unauthenticated.
    public string? ScheduledTickToken { get; set; }
}
