// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Security.Admission;

namespace Honua.Geoprocessing.Inference;

internal sealed record ImageryInferenceRequest
{
    // The owner and job scope are validated by the server and are never sent to the model provider.
    [JsonIgnore]
    public AiExecutionContext? ExecutionContext { get; init; }

    public required string Model { get; init; }

    public required string Task { get; init; }

    public required byte[] ImageBytes { get; init; }

    public int SourceCrsCode { get; init; }

    public double? ConfidenceThreshold { get; init; }

    public required long MaxArtifactBytes { get; init; }
}

internal enum ImageryInferenceOutputType
{
    Raster,

    Features
}

internal sealed record ImageryInferenceOutcome
{
    public required ImageryInferenceOutputType OutputType { get; init; }

    public byte[]? RasterBytes { get; init; }

    public byte[]? FeatureCollectionJson { get; init; }
}

// The failure text must not contain the endpoint URL, credentials, or a raw provider body.
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
