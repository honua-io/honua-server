// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Styling.Domain;

public enum ClassificationMethod
{
    EqualInterval,

    Quantile,

    NaturalBreaks,

    UniqueValue
}

public sealed class ClassificationResult
{
    public required ClassificationMethod Method { get; init; }

    public required string FieldName { get; init; }

    public double[]? Breaks { get; init; }

    public string[]? Categories { get; init; }

    public int ClassCount { get; init; }
}
