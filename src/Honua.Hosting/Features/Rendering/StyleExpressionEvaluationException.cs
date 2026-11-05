// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Rendering;

// An unevaluable style throws instead of coercing to zero, which painted color ramps black.
public sealed class StyleExpressionEvaluationException : Exception
{
    public StyleExpressionEvaluationException(string message)
        : base(message)
    {
    }

    public StyleExpressionEvaluationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
