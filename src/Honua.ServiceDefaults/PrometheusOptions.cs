// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.ServiceDefaults;

/// <summary>
/// Configuration options for native Prometheus text exposition.
/// </summary>
public sealed class PrometheusOptions
{
    /// <summary>
    /// Configuration section name.
    /// </summary>
    public const string SectionName = "Observability:Prometheus";

    /// <summary>
    /// Gets or sets the HTTP path used by Prometheus to scrape metrics.
    /// </summary>
    public string Path { get; set; } = "/metrics";
}
