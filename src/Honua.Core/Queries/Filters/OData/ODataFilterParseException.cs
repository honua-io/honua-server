// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Queries.Filters.OData;

public sealed class ODataFilterParseException : ArgumentException
{
    public ODataFilterParseException(string message, int position)
        : base($"{message} (position {position}).")
    {
        Position = position;
    }

    public int Position { get; }
}
