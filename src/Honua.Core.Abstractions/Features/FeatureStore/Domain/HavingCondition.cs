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
    /// The numeric literal compared against a numeric aggregate. Bound as a query
    /// parameter rather than concatenated into SQL. Min/max on a string or temporal
    /// field leaves this unused and sets the matching typed literal instead.
    /// </summary>
    public required double Value { get; init; }

    /// <summary>
    /// Quoted string compared against min/max of a string field.
    /// </summary>
    public string? TextValue { get; init; }

    /// <summary>
    /// Timestamp compared against min/max of a datetime field.
    /// </summary>
    public DateTimeOffset? TimestampValue { get; init; }

    /// <summary>
    /// Calendar date compared against min/max of a date field.
    /// </summary>
    public DateOnly? DateValue { get; init; }

    /// <summary>
    /// Time of day compared against min/max of a time field.
    /// </summary>
    public TimeOnly? TimeValue { get; init; }

    /// <summary>
    /// Optional field type hint used to emit correctly typed aggregates for
    /// JSON-backed fields, mirroring <see cref="StatisticDefinition.FieldType"/>.
    /// </summary>
    public MetadataV2FieldType? FieldType { get; init; }

    /// <summary>
    /// The comparison literal bound into SQL. Numeric aggregates use <see cref="Value"/>.
    /// Min/max on a string or temporal field uses that field's literal so the provider
    /// does not compare the aggregate to a double.
    /// </summary>
    /// <returns>The parameter value for this condition.</returns>
    public object BoundValue()
    {
        if (TextValue is not null)
        {
            return TextValue;
        }

        if (TimestampValue is { } timestamp)
        {
            return timestamp;
        }

        if (DateValue is { } date)
        {
            return date;
        }

        if (TimeValue is { } time)
        {
            return time;
        }

        return Value;
    }
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
