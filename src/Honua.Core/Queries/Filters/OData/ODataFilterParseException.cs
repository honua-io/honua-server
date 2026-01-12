// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Queries.Filters.OData;

/// <summary>
/// Represents errors that occur while parsing OData filter expressions.
/// </summary>
public sealed class ODataFilterParseException : ArgumentException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ODataFilterParseException"/> class.
    /// </summary>
    /// <param name="message">Error message describing the parse failure.</param>
    /// <param name="position">Position in the input where the error occurred.</param>
    public ODataFilterParseException(string message, int position)
        : base($"{message} (position {position}).")
    {
        Position = position;
    }

    /// <summary>
    /// Gets the position in the input where the error occurred.
    /// </summary>
    public int Position { get; }
}
