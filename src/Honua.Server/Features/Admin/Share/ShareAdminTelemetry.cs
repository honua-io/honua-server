// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics.Metrics;
using Honua.ServiceDefaults;

namespace Honua.Server.Features.Admin.Share;

internal static class ShareAdminTelemetry
{
    private static readonly Counter<long> ExportTriggered = HonuaTelemetry.Meter.CreateCounter<long>(
        "honua.share.export.triggered",
        unit: "{run}",
        description: "Share export trigger attempts by destination status.");

    public static void RecordExportTriggered(string destinationType, string destinationStatus)
        => ExportTriggered.Add(
            1,
            new KeyValuePair<string, object?>("honua.share.destination_type", destinationType),
            new KeyValuePair<string, object?>("honua.share.destination_status", destinationStatus));
}
