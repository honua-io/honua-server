// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Core.Features.Metadata.Domain.V2;

public enum MetadataV2WmsProtectedLayerMode
{
    Hide,
    Challenge,
}

// How a WMS service presents layers the caller may not read. "hide" (the default) leaves them out of
// capabilities and answers requests for them as undefined layers. "challenge" lists them by name only
// and answers requests for them with an authentication challenge (401) or a denial (403).
public static class MetadataV2WmsProtectedLayers
{
    public const string OptionName = "wmsProtectedLayers";

    public const string Hide = "hide";

    public const string Challenge = "challenge";

    // Null when the option is present but is not one of the two modes.
    public static MetadataV2WmsProtectedLayerMode? ResolveMode(MetadataV2Service service)
    {
        ArgumentNullException.ThrowIfNull(service);
        if (!service.Options.TryGetValue(OptionName, out var value))
        {
            return MetadataV2WmsProtectedLayerMode.Hide;
        }

        var mode = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (string.Equals(mode, Hide, StringComparison.OrdinalIgnoreCase))
        {
            return MetadataV2WmsProtectedLayerMode.Hide;
        }

        return string.Equals(mode, Challenge, StringComparison.OrdinalIgnoreCase)
            ? MetadataV2WmsProtectedLayerMode.Challenge
            : null;
    }

    // An unreadable value keeps the hiding default: a malformed option must never advertise layers.
    public static bool ChallengesProtectedLayers(MetadataV2Service service)
        => ResolveMode(service) == MetadataV2WmsProtectedLayerMode.Challenge;
}
