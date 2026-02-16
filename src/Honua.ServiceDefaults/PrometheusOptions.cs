// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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
