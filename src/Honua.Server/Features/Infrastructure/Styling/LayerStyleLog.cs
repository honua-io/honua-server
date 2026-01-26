// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Logging;

namespace Honua.Server.Features.Infrastructure.Styling;

/// <summary>
/// Source-generated logging for style operations.
/// </summary>
internal static partial class LayerStyleLog
{
    [LoggerMessage(
        EventId = 6400,
        Level = LogLevel.Warning,
        Message = "Unsupported GeoServices renderer type '{RendererType}' for layer {LayerId}. Falling back to default MapLibre style.")]
    public static partial void UnsupportedRendererType(ILogger logger, string rendererType, int layerId);
}
