// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics.Metrics;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Logging;

namespace Honua.Protocols.Ogc.Classic;

internal static class CapabilitiesLayerBuilder
{
    private static readonly Counter<long> OmittedLayers =
        HonuaTelemetry.Meter.CreateCounter<long>("honua.ogc.capabilities.omitted_layers");

    internal static async Task<T?> TryBuildAsync<T>(Func<Task<T>> build, string publicationId, ILogger logger)
        where T : class
    {
        try
        {
            return await build().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            var errorClass = exception.GetType().Name;
            OmittedLayers.Add(1, new(HonuaTelemetry.Tags.LayerId, publicationId), new("error.type", errorClass));
            OgcClassicLog.LayerOmitted(logger, publicationId, errorClass);
            return null;
        }
    }
}
