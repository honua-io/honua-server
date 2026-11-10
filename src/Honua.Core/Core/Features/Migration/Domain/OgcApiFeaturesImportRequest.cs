// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
namespace Honua.Core.Features.Migration.Domain;

public sealed record OgcApiFeaturesImportRequest
{
    public required string ServiceUrl { get; init; }

    public required string CollectionId { get; init; }

    public required string TargetSchema { get; init; }

    public string? TargetTable { get; init; }

    public int PageSize { get; init; } = 500;

    public int MaxFeatures { get; init; }

    public int TimeoutSeconds { get; init; } = 300;

    public int MaxPages { get; init; } = 1_000;

    public bool AllowUnsafeLocalUrls { get; init; }

    public IReadOnlyCollection<string>? AllowedServiceHostSuffixes { get; init; }

    public string? Filter { get; init; }

    // The bbox must be four or six finite numbers with each maximum at least its minimum; any other shape is rejected.
    public double[]? Bbox { get; init; }

    public string? Datetime { get; init; }
}
