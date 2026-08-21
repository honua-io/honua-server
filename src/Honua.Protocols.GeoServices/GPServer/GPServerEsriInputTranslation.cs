// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Geoprocessing;

namespace Honua.Protocols.GeoServices.GPServer;

/// <summary>
/// GPServer facade over the protocol-neutral Esri geometry/FeatureSet translator.
/// MCP and GPServer therefore derive the same canonical WKB and SRID inputs and
/// return the same honest capability message for multi-feature payloads.
/// </summary>
internal static class GPServerEsriInputTranslation
{
    internal readonly record struct EsriInputTranslationResult(
        Dictionary<string, string> Inputs,
        bool RequiresFeatureCollectionExecution,
        string? CapabilityMessage,
        int? InputSpatialReference)
    {
        public bool Translated { get; init; }
    }

    public static EsriInputTranslationResult Translate(IReadOnlyDictionary<string, string> inputs)
    {
        var result = EsriGpInputTranslation.Translate(inputs);
        return new EsriInputTranslationResult(
            result.Inputs,
            result.RequiresFeatureCollectionExecution,
            result.CapabilityMessage,
            result.InputSpatialReference)
        {
            Translated = result.Translated
        };
    }
}
