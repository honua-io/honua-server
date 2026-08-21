// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics.Metrics;
using Honua.ServiceDefaults;

namespace Honua.Protocols.Ogc.Api.Processes;

internal static class OgcProcessProjectionTelemetry
{
    private static int _projectedProcessCount;

    private static readonly ObservableGauge<int> ProjectedProcesses = HonuaTelemetry.Meter.CreateObservableGauge(
        "honua.gp.ogc_projected_processes",
        () => Volatile.Read(ref _projectedProcessCount),
        description: "Number of built-in geoprocessing catalog entries directly projected through OGC API Processes.");

    internal static void SetProjectedProcessCount(int count)
    {
        _ = ProjectedProcesses;
        Volatile.Write(ref _projectedProcessCount, count);
    }
}
