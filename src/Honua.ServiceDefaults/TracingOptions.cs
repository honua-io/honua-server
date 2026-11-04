// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.ServiceDefaults;

public sealed class TracingOptions
{
    public const string SectionName = "Tracing";

    public bool Enabled { get; set; } = true;

    public double SamplingRatio { get; set; } = 1.0;

    public bool IncludeDbStatementText { get; set; }

    public bool TraceHealthEndpoints { get; set; }

    public bool ExportExceptionDetails { get; set; }

    public bool RecordExceptionStackTraces { get; set; }

    public int MaxExceptionDetailLength { get; set; } = 256;

    public int ServingLatencyWindowSeconds { get; set; } = 300;

    public int ServingLatencySamplesPerProtocol { get; set; } = 4096;

    public int MaxAttributesPerSpan { get; set; } = 128;

    public int MaxEventsPerSpan { get; set; } = 128;

    public string? OtlpEndpoint { get; set; }

    public string? OtlpHeaders { get; set; }

    public XRayTracingOptions XRay { get; set; } = new();
}

public sealed class XRayTracingOptions
{
    public bool Enabled { get; set; }
}
