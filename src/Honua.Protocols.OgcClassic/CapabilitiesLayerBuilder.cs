// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace Honua.Protocols.Ogc.Classic;

internal static partial class CapabilitiesLayerBuilder
{
    private static readonly Meter Meter = new("Honua.Protocols.Ogc.Capabilities");
    private static readonly Counter<long> OmittedLayers = Meter.CreateCounter<long>("honua.ogc.capabilities.omitted_layers");

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
            OmittedLayers.Add(1, new("layer.id", publicationId), new("error.type", errorClass));
            LayerOmitted(logger, publicationId, errorClass);
            return null;
        }
    }

    [LoggerMessage(EventId = 5646, Level = LogLevel.Warning,
        Message = "OGC capabilities omitted layer {PublicationId}: {ErrorClass}")]
    private static partial void LayerOmitted(ILogger logger, string publicationId, string errorClass);
}
