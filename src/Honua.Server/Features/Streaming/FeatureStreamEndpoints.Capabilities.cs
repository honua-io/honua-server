// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Server.Features.Streaming;

internal static partial class FeatureStreamEndpoints
{
    private const int BaselineGuidanceMaxProbeFeatures = 5000;
    private static readonly TimeSpan BaselineGuidanceLookupBudget = TimeSpan.FromSeconds(1);

    private static async Task<bool?> ProbeBaselineCompletionAsync(
        FeatureStreamDependencies deps,
        int layerId,
        HttpContext context,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        context.RequestAborted.ThrowIfCancellationRequested();
        if (cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        try
        {
            var featureCap = deps.Options.Value.MaxSnapshotFeatures;
            var probeLimit = Math.Min(featureCap, BaselineGuidanceMaxProbeFeatures) + 1;
            var ids = await deps.FeatureReader.QueryObjectIdsAsync(layerId, new FeatureQuery { Limit = probeLimit }, cancellationToken)
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            // Visible ids beyond the cap prove truncation; fewer ids cannot prove the byte budget fits.
            return ids.Length > featureCap ? false : null;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException && !context.RequestAborted.IsCancellationRequested)
        {
            FeatureStreamLog.BaselineGuidanceUnavailable(logger, layerId, exception);
            return null;
        }
    }
}
