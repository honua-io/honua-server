// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Core.Features.Scene.Conversion;

/// <summary>
/// Parses Esri I3S scene-layer descriptors served by a SceneServer at
/// <c>/SceneServer/layers/0</c>.
/// </summary>
/// <remarks>
/// This reader performs no geometry decode; it only maps the JSON descriptor
/// into the strongly-typed <see cref="I3sSceneLayerDocument"/> model.
/// </remarks>
public static class I3sSceneLayerReader
{
    /// <summary>
    /// Parses a raw <c>3dSceneLayer.json</c> byte payload into the descriptor
    /// model. Throws <see cref="I3sConversionException"/> with
    /// <see cref="I3sConversionErrorReason.MalformedSceneLayer"/> when the bytes
    /// are not valid JSON or do not bind to the descriptor shape.
    /// </summary>
    /// <param name="utf8Json">UTF-8 encoded scene-layer JSON.</param>
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
