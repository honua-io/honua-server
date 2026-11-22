// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
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
            CapabilitiesBuildDegradation.Mark();
            return null;
        }
    }
}

// A capabilities document that dropped a layer, TIME dimension or legend after a transient error is
// served to its own request but must not be cached for every other caller.
internal static class CapabilitiesBuildDegradation
{
    private static readonly AsyncLocal<StrongBox<bool>?> Current = new();

    internal static StrongBox<bool> Begin()
    {
        var degraded = new StrongBox<bool>();
        Current.Value = degraded;
        return degraded;
    }

    internal static void Mark()
    {
        if (Current.Value is { } degraded)
        {
            degraded.Value = true;
        }
    }
}
