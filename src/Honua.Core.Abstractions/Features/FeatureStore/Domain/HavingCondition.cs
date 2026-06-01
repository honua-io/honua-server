// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.FeatureStore.Domain;

/// <summary>
/// A single aggregate comparison applied after grouping in a statistics query,
/// modelling one term of a SQL <c>HAVING</c> clause (for example
/// <c>COUNT(objectid) &gt; 5</c> or <c>SUM(pop) &gt;= 1000</c>).
/// </summary>
/// <remarks>
/// The condition is intentionally structured rather than raw SQL: the provider
/// rebuilds the aggregate expression from <see cref="StatisticType"/> and the
/// validated <see cref="OnStatisticField"/>, applies the validated
/// <see cref="Operator"/>, and binds <see cref="Value"/> as a query parameter.
/// This keeps the HAVING clause on the same safe, parameterized path as the
/// WHERE clause and prevents arbitrary SQL passthrough.
/// </remarks>
public readonly record struct HavingCondition
{
    /// <summary>
    /// The aggregate function applied to <see cref="OnStatisticField"/>.
    /// </summary>
    public required StatisticType StatisticType { get; init; }

    /// <summary>
    /// The field the aggregate is computed over. Must be a valid layer field
    /// (or the object-id field) and is re-validated by the provider.
    /// </summary>
    public required string OnStatisticField { get; init; }

    /// <summary>
    /// The comparison operator applied between the aggregate and <see cref="Value"/>.
    /// </summary>
    public required HavingComparisonOperator Operator { get; init; }

    /// <summary>
    /// The numeric literal compared against the aggregate. Bound as a query
    /// parameter rather than concatenated into SQL.
    /// </summary>
    public required double Value { get; init; }

    /// <summary>
    /// Optional field type hint used to emit correctly typed aggregates for
    /// JSON-backed fields, mirroring <see cref="StatisticDefinition.FieldType"/>.
    /// </summary>
    public MetadataV2FieldType? FieldType { get; init; }
}

/// <summary>
/// Comparison operators supported in an aggregate <c>HAVING</c> condition.
/// </summary>
public enum HavingComparisonOperator
{
    /// <summary>Equality (<c>=</c>).</summary>
    Equal,

    /// <summary>Inequality (<c>&lt;&gt;</c>).</summary>
    NotEqual,

    /// <summary>Greater than (<c>&gt;</c>).</summary>
    GreaterThan,

    /// <summary>Greater than or equal (<c>&gt;=</c>).</summary>
    GreaterThanOrEqual,

    /// <summary>Less than (<c>&lt;</c>).</summary>
    LessThan,

    /// <summary>Less than or equal (<c>&lt;=</c>).</summary>
    LessThanOrEqual
}
