// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Geoprocessing.Abstractions;

/// <summary>Translates Esri GP geometry payloads into canonical process inputs.</summary>
public interface IEsriGeoprocessingInputTranslator
{
    EsriGeoprocessingInputTranslation Translate(IReadOnlyDictionary<string, string> inputs);
}

/// <summary>Result of translating Esri GP inputs.</summary>
public readonly record struct EsriGeoprocessingInputTranslation(
    Dictionary<string, string> Inputs,
    bool RequiresFeatureCollectionExecution,
    string? CapabilityMessage,
    int? InputSpatialReference,
    bool Translated);
