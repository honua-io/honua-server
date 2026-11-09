// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Server.Features.Streaming;

internal static partial class FeatureStreamEndpoints
{
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
            var count = await deps.FeatureReader.CountAsync(layerId, new FeatureQuery(), cancellationToken)
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            // The shared reader enforces row visibility. Estimates cannot prove an overflow,
            // and a count inside the feature cap cannot prove the byte budget will fit.
            return count > deps.Options.Value.MaxSnapshotFeatures ? false : null;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException && !context.RequestAborted.IsCancellationRequested)
        {
            FeatureStreamLog.BaselineGuidanceUnavailable(logger, layerId, exception);
            return null;
        }
    }
}
