// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;

namespace Honua.Core.Configuration;

public sealed class GeometryValidationOptions
{
    public const string SectionName = "GeometryValidation";

    [Range(1024, 50 * 1024 * 1024)] // 1KB to 50MB
    public int MaxWkbSize { get; set; } = 10 * 1024 * 1024; // 10MB default

    [Range(10, 1_000_000)]
    public int MaxVertices { get; set; } = 100_000;

    [Range(1, 10_000)]
    public int MaxRings { get; set; } = 1_000;

    public bool StrictValidation { get; set; } = true;

    public bool ValidateCoordinatePrecision { get; set; }

    [Range(1, 15)]
    public int MaxCoordinatePrecision { get; set; } = 10;

    public bool AllowNullGeometry { get; set; }

    public bool EnableTopologyValidation { get; set; } = true;

    public bool EnableAutoRepair { get; set; } = true;

    public ValidationMode Mode { get; set; } = ValidationMode.Repair;

    public TimeSpan ValidationTimeout { get; set; } = TimeSpan.FromSeconds(30);

    [Range(0d, 1d)]
    public double RingClosureTolerance { get; set; } = 1e-9;
}

public enum ValidationMode
{
    Accept,

    Strict,

    Repair
}
