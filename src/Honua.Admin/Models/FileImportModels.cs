// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Admin.Models;

public sealed record FilePreviewResponse
{
    public string Format { get; init; } = string.Empty;
    public int TotalFeatureCount { get; init; }
    public int? DetectedSrid { get; init; }
    public Dictionary<string, object?> SampleProperties { get; init; } = new();
    public string[] AvailableLayers { get; init; } = [];
}

public sealed record FileImportResult
{
    public bool Success { get; init; }
    public int FeatureCount { get; init; }
    public string TableName { get; init; } = string.Empty;
    public string Format { get; init; } = string.Empty;
    public int? DetectedSrid { get; init; }
    public string? ErrorMessage { get; init; }
}
