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

public sealed record OgcApiFeaturesMigrationSourceSnapshot
{
    public required string BaseUrl { get; init; }

    public string? Title { get; init; }

    public string? Description { get; init; }

    public string? Version { get; init; }

    public OgcApiFeaturesLink[] LandingPageLinks { get; init; } = [];

    public string[] ConformanceClasses { get; init; } = [];

    public OgcApiFeaturesCollectionSnapshot[] Collections { get; init; } = [];

    public OgcApiFeaturesCrsDeclaration[] CrsDeclarations { get; init; } = [];

    public string[] VendorExtensions { get; init; } = [];
}

public sealed record OgcApiFeaturesCollectionSnapshot
{
    public required string Id { get; init; }

    public string? Title { get; init; }

    public string? Description { get; init; }

    public string? GeometryType { get; init; }

    public int? FeatureCount { get; init; }

    public OgcApiFeaturesLink[] Links { get; init; } = [];

    public OgcApiFeaturesLink[] PaginationLinks { get; init; } = [];

    public OgcApiFeaturesCrsDeclaration[] CrsDeclarations { get; init; } = [];

    public string[] ItemEncodings { get; init; } = [];

    public MigrationInventoryField[] Fields { get; init; } = [];

    public string[] VendorExtensions { get; init; } = [];
}

public sealed record OgcApiFeaturesLink
{
    public required string Rel { get; init; }

    public required string Href { get; init; }

    public string? Type { get; init; }

    public string? Title { get; init; }
}

public sealed record OgcApiFeaturesCrsDeclaration
{
    public required string Role { get; init; }

    public required string Value { get; init; }
}
