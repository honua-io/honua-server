// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.ServiceDefaults;

public sealed class PrometheusOptions
{
    public const string SectionName = "Observability:Prometheus";

    public string Path { get; set; } = "/metrics";
}
