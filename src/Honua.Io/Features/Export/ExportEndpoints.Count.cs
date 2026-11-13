// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.ServiceDefaults;

namespace Honua.Io.Export;

internal static partial class ExportEndpoints
{
    private sealed record ExportCountRequest(int StorageLayerId, FeatureQuery Query, CancellationToken CancellationToken)
    {
        public required string ServiceName { get; init; }
        public required int ServiceLayerIndex { get; init; }
    }

    private static async Task<long> CountFeaturesAsync(
        IFeatureReader featureReader,
        ExportCountRequest request,
        Activity? activity)
    {
        var countSw = Stopwatch.StartNew();
        using (var countActivity = HonuaTelemetry.StartDatabaseActivity(
            "export.count",
            request.ServiceLayerIndex.ToString(CultureInfo.InvariantCulture)))
        {
            countActivity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.Admin);
            countActivity?.SetTag(HonuaTelemetry.Tags.ServiceId, request.ServiceName);
            try
            {
                var count = await featureReader.CountAsync(request.StorageLayerId, request.Query, request.CancellationToken);
                HonuaTelemetry.SetSuccess(countActivity, ToTelemetryFeatureCount(count));
                return count;
            }
            catch (Exception ex)
            {
                HonuaTelemetry.RecordException(countActivity, ex);
                HonuaTelemetry.RecordException(activity, ex);
                throw;
            }
            finally
            {
                HonuaTelemetry.CategorizeLatency(countActivity, countSw.Elapsed.TotalMilliseconds);
            }
        }
    }
}
