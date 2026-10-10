// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Core.Features.Metadata.Domain.V2;

/// <summary>Resolves a MapServer publication service's drawing contract.</summary>
/// <remarks>The mode applies to the whole map service, including all of its publications.
/// Existing services default to cached drawing. Dynamic drawing does not imply support
/// for the separate ArcGIS dynamicLayers contract.</remarks>
public static class MetadataV2MapServerDrawing
{
    /// <summary>Service option selecting cached or dynamic MapServer drawing.</summary>
    public const string OptionName = "mapServerDrawingMode";

    /// <summary>Cached drawing mode, including tile and tile-export operations.</summary>
    public const string Cached = "cached";

    /// <summary>Dynamic map-image drawing mode without a cache or tile exports.</summary>
    public const string Dynamic = "dynamic";

    /// <summary>Validates the explicit mode and resolves its cached default.</summary>
    /// <param name="service">Service hosting the map publications.</param>
    /// <param name="cached">Whether the service declares cached drawing.</param>
    /// <returns>False for an invalid explicit value; true for an absent or valid option.</returns>
    public static bool TryResolveCachedDrawing(MetadataV2Service service, out bool cached)
    {
        ArgumentNullException.ThrowIfNull(service);
        cached = true;
        if (!service.Options.TryGetValue(OptionName, out var value))
        {
            return true;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var mode = value.GetString();
        if (string.Equals(mode, Cached, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(mode, Dynamic, StringComparison.OrdinalIgnoreCase))
        {
            cached = false;
            return true;
        }

        return false;
    }

    /// <summary>Resolves a validated service's drawing mode, rejecting invalid persisted values.</summary>
    /// <param name="service">Service hosting the map publications.</param>
    /// <returns>True for cached drawing, false for dynamic drawing.</returns>
    /// <exception cref="InvalidOperationException">The service has an invalid explicit mode.</exception>
    public static bool UsesCachedDrawing(MetadataV2Service service)
        => TryResolveCachedDrawing(service, out var cached)
            ? cached
            : throw new InvalidOperationException($"Service '{service.Metadata.Id}' has an invalid {OptionName} option.");
}
