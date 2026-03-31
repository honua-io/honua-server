// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Styling.Domain;

/// <summary>
/// Classification method used to divide values into classes.
/// </summary>
public enum ClassificationMethod
{
    /// <summary>Breaks at equal intervals between min and max.</summary>
    EqualInterval,

    /// <summary>Breaks at quantile (percentile) boundaries.</summary>
    Quantile,

    /// <summary>Breaks that minimize within-class variance (Jenks).</summary>
    NaturalBreaks,

    /// <summary>Each distinct value maps to its own class.</summary>
    UniqueValue
}

/// <summary>
/// Output of a classification algorithm applied to a field's values.
/// </summary>
public sealed class ClassificationResult
{
    /// <summary>
    /// The classification method used.
    /// </summary>
    public required ClassificationMethod Method { get; init; }

    /// <summary>
    /// Name of the field that was classified.
    /// </summary>
    public required string FieldName { get; init; }

    /// <summary>
    /// Break values for numeric classification (null for UniqueValue).
    /// </summary>
    public double[]? Breaks { get; init; }

    /// <summary>
    /// Distinct category values for UniqueValue classification (null for numeric).
    /// </summary>
    public string[]? Categories { get; init; }

    /// <summary>
    /// Number of classes produced.
    /// </summary>
    public int ClassCount { get; init; }
}
