// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Core.Features.FileImport.Services.FileGdb;
namespace Honua.Core.Features.FileImport.Domain;

/// <summary>
/// Advisory schema suggestions for imported data, computed from preview analysis.
/// Suggestions are declarative and never silently applied.
/// </summary>
public sealed class SchemaSuggestion
{
    /// <summary>
    /// The source file name or data source identifier.
    /// </summary>
    public required string SourceName { get; init; }

    /// <summary>
    /// Recommended SRID for the data, with justification.
    /// </summary>
    public required SridSuggestion Srid { get; init; }

    /// <summary>
    /// Recommended indexes for the imported table.
    /// </summary>
    public IReadOnlyList<IndexSuggestion> Indexes { get; init; } = [];

    /// <summary>
    /// Field-level type recommendations.
    /// </summary>
    public IReadOnlyList<FieldTypeSuggestion> FieldTypes { get; init; } = [];

    /// <summary>
    /// The detected file format.
    /// </summary>
    public required string DetectedFormat { get; init; }

    /// <summary>
    /// General observations about the data.
    /// </summary>
    public IReadOnlyList<string> Observations { get; init; } = [];
}

/// <summary>
/// SRID recommendation for imported data.
/// </summary>
public sealed class SridSuggestion
{
    /// <summary>
    /// The recommended SRID.
    /// </summary>
    public required int RecommendedSrid { get; init; }

    /// <summary>
    /// The detected source SRID (may differ from recommendation).
    /// </summary>
    public required int DetectedSrid { get; init; }

    /// <summary>
    /// Justification for the recommendation.
    /// </summary>
    public required string Reason { get; init; }

    /// <summary>
    /// Whether transformation is recommended.
    /// </summary>
    public bool RequiresTransformation => RecommendedSrid != DetectedSrid;
}

/// <summary>
/// Index recommendation for an imported table.
/// </summary>
public sealed class IndexSuggestion
{
    /// <summary>
    /// Type of index suggested.
    /// </summary>
    public required IndexSuggestionType Type { get; init; }

    /// <summary>
    /// Column(s) the index should cover.
    /// </summary>
    public required IReadOnlyList<string> Columns { get; init; }

    /// <summary>
    /// Justification for the index.
    /// </summary>
    public required string Reason { get; init; }
}

/// <summary>
/// Types of index suggestions.
/// </summary>
public enum IndexSuggestionType
{
    /// <summary>Spatial index (GIST) for geometry column.</summary>
    Spatial,

    /// <summary>B-tree index for primary key or frequently queried fields.</summary>
    BTree,

    /// <summary>GIN index for text search or JSON fields.</summary>
    Gin
}

/// <summary>
/// Field type recommendation based on data analysis.
/// </summary>
public sealed class FieldTypeSuggestion
{
    /// <summary>
    /// The field name.
    /// </summary>
    public required string FieldName { get; init; }

    /// <summary>
    /// The detected type from the source data.
    /// </summary>
    public required string DetectedType { get; init; }

    /// <summary>
    /// The recommended PostgreSQL type.
    /// </summary>
    public required string RecommendedType { get; init; }

    /// <summary>
    /// Justification for the recommendation.
    /// </summary>
    public required string Reason { get; init; }
}
