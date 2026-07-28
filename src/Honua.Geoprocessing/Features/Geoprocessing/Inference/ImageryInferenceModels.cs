// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Geoprocessing.Inference;

/// <summary>
/// One delegated inference invocation: a source raster plus a model reference.
/// Models are always references into the configured backend (an endpoint's
/// deployed model name) — Honua never executes the model itself.
/// </summary>
internal sealed record ImageryInferenceRequest
{
    /// <summary>Model reference passed verbatim to the backend.</summary>
    public required string Model { get; init; }

    /// <summary>Inference task hint: classification, segmentation, or detection.</summary>
    public required string Task { get; init; }

    /// <summary>Source raster bytes (GeoTIFF).</summary>
    public required byte[] ImageBytes { get; init; }

    /// <summary>Optional score threshold in [0, 1] forwarded to the backend.</summary>
    public double? ConfidenceThreshold { get; init; }

    /// <summary>
    /// Artifact size ceiling the executor will enforce; adapters use it to cap
    /// response buffering so a misbehaving backend cannot exhaust worker memory.
    /// </summary>
    public required long MaxArtifactBytes { get; init; }
}

/// <summary>Shape of a successful delegated inference result.</summary>
internal enum ImageryInferenceOutputType
{
    /// <summary>A classification/segmentation raster (GeoTIFF, georeferencing preserved).</summary>
    Raster,

    /// <summary>Detected features as a GeoJSON FeatureCollection in the source CRS.</summary>
    Features
}

/// <summary>Validated result returned by a provider adapter.</summary>
internal sealed record ImageryInferenceOutcome
{
    /// <summary>Which artifact shape the backend produced.</summary>
    public required ImageryInferenceOutputType OutputType { get; init; }

    /// <summary>GeoTIFF bytes when <see cref="OutputType"/> is <see cref="ImageryInferenceOutputType.Raster"/>.</summary>
    public byte[]? RasterBytes { get; init; }

    /// <summary>UTF-8 GeoJSON FeatureCollection when <see cref="OutputType"/> is <see cref="ImageryInferenceOutputType.Features"/>.</summary>
    public byte[]? FeatureCollectionJson { get; init; }
}

/// <summary>
/// Delegation failure whose <see cref="Exception.Message"/> is safe to surface on
/// the job status: adapters must never place endpoint URLs, credentials, or raw
/// provider response bodies in it.
/// </summary>
internal sealed class ImageryInferenceException : Exception
{
    public ImageryInferenceException(string safeMessage)
        : base(safeMessage)
    {
    }

    public ImageryInferenceException(string safeMessage, Exception innerException)
        : base(safeMessage, innerException)
    {
    }
}
