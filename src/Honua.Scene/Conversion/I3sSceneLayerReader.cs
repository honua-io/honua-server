// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Core.Features.Scene.Conversion;

public static class I3sSceneLayerReader
{
    public static I3sSceneLayerDocument Parse(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            var document = JsonSerializer.Deserialize(
                utf8Json,
                I3sSceneLayerJsonContext.Default.I3sSceneLayerDocument);

            if (document is null)
            {
                throw new I3sConversionException(
                    I3sConversionErrorReason.MalformedSceneLayer,
                    "The I3S scene-layer descriptor was empty or null.");
            }

            return document;
        }
        catch (JsonException ex)
        {
            throw new I3sConversionException(
                I3sConversionErrorReason.MalformedSceneLayer,
                "The I3S scene-layer descriptor was not valid JSON.",
                ex);
        }
    }

}
