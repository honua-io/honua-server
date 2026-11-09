// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Core.Features.Metadata.Domain.V2;

public static class MetadataV2MapServerDrawing
{
    public const string OptionName = "mapServerDrawingMode";

    public const string Cached = "cached";

    public const string Dynamic = "dynamic";

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

    public static bool UsesCachedDrawing(MetadataV2Service service)
        => TryResolveCachedDrawing(service, out var cached)
            ? cached
            : throw new InvalidOperationException($"Service '{service.Metadata.Id}' has an invalid {OptionName} option.");
}
