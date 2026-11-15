// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Queries.Filters;

public sealed class UnevaluableLikePatternException : Exception
{
    public UnevaluableLikePatternException()
        : base("LIKE pattern could not be evaluated.")
    {
    }
}
