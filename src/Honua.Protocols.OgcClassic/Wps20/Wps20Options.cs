// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Protocols.Ogc.Classic.Wps20;

internal sealed class Wps20Options
{
    public const string SectionName = "Wps20";

    public bool EnableConformanceEcho { get; set; }

    public string ConformanceEchoProcessId { get; set; } = "honua.cite.echo";

    public string[] ConformanceReferenceAllowedHosts { get; set; } = ["raw.githubusercontent.com"];

    public int ConformanceJobCapacity { get; set; } = 128;

    public int ConformanceJobTtlSeconds { get; set; } = 600;
}
