// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Extensions.Configuration;

namespace Honua.Core.Features.ControlPlane.Abstractions;

public static class ControlPlaneTriggerModeResolver
{
    private const string TriggerModeKey = "ControlPlane:TriggerMode";
    private const string EventModeValue = "Event";

    public static bool IsEventMode(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var value = configuration[TriggerModeKey];
        return string.Equals(value?.Trim(), EventModeValue, StringComparison.OrdinalIgnoreCase);
    }

    public static bool ShouldHostInProcessTimers(IConfiguration configuration)
        => !IsEventMode(configuration);
}
