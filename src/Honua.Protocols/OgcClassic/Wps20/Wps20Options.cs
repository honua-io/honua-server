// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Protocols.Ogc.Classic.Wps20;

internal sealed class Wps20Options
{
    public const string SectionName = "Wps20";

    public bool EnableConformanceEcho { get; set; }

    public string ConformanceEchoProcessId { get; set; } = "honua.cite.echo";

    public string[] ConformanceReferenceAllowedHosts { get; set; } = ["raw.githubusercontent.com"];

    public string? PublicBaseUrl { get; set; }

    public int ConformanceJobCapacity { get; set; } = 128;

    public int ConformanceJobTtlSeconds { get; set; } = 600;
}
