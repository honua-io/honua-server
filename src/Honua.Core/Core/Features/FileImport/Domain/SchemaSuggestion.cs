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
namespace Honua.Core.Features.FileImport.Domain;

// Schema suggestions are declarative and are never applied silently.
public sealed class SchemaSuggestion
{
    public required string SourceName { get; init; }

    public required SridSuggestion Srid { get; init; }

    public IReadOnlyList<IndexSuggestion> Indexes { get; init; } = [];

    public IReadOnlyList<FieldTypeSuggestion> FieldTypes { get; init; } = [];

    public required string DetectedFormat { get; init; }

    public IReadOnlyList<string> Observations { get; init; } = [];
}

public sealed class SridSuggestion
{
    public required int RecommendedSrid { get; init; }

    public required int DetectedSrid { get; init; }

    public required string Reason { get; init; }

    public bool RequiresTransformation => RecommendedSrid != DetectedSrid;
}

public sealed class IndexSuggestion
{
    public required IndexSuggestionType Type { get; init; }

    public required IReadOnlyList<string> Columns { get; init; }

    public required string Reason { get; init; }
}

public enum IndexSuggestionType
{
    Spatial,

    BTree,

    Gin
}

public sealed class FieldTypeSuggestion
{
    public required string FieldName { get; init; }

    public required string DetectedType { get; init; }

    public required string RecommendedType { get; init; }

    public required string Reason { get; init; }
}
