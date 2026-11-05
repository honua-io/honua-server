// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;

namespace Honua.Server.Features.Operations.Status;

internal sealed class OperateSloOptions
{
    public const string SectionName = "Slo";

    public AvailabilityOptions Availability { get; set; } = new();

    public bool HasAvailabilityTarget
        => Availability.Target is > 0 and < 1;
}

internal sealed class AvailabilityOptions
{
    [Range(0d, 1d)]
    public double? Target { get; set; }

    [Range(1, 30 * 24 * 60 * 60)]
    public int RollingWindowSeconds { get; set; } = 300;
}
