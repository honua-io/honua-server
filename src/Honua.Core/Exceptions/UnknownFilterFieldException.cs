// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Exceptions;

/// <summary>
/// Exception thrown when a filter expression references a property the target
/// resource does not declare.
/// </summary>
/// <remarks>
/// Derives from <see cref="ArgumentException"/> so every existing handler that
/// maps filter-normalization failures to a 400 keeps working unchanged. The
/// distinct type exists so a caller that fans one filter out across several
/// resources — STAC item search across collections — can tell "this collection
/// does not carry that property" apart from "this filter is invalid" and let the
/// non-matching collection contribute no items instead of failing the whole
/// request (honua-server#3392).
/// </remarks>
public sealed class UnknownFilterFieldException : ArgumentException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="UnknownFilterFieldException"/> class.
    /// </summary>
    public UnknownFilterFieldException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="UnknownFilterFieldException"/> class
    /// with a specified error message.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    public UnknownFilterFieldException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="UnknownFilterFieldException"/> class
    /// with a specified error message and a reference to the inner exception that is the
    /// cause of this exception.
    /// </summary>
    /// <param name="message">The error message that explains the reason for the exception.</param>
    /// <param name="innerException">The exception that is the cause of the current exception.</param>
    public UnknownFilterFieldException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Gets the filter property that could not be resolved, when known.
    /// </summary>
    public string? PropertyName { get; private init; }

    /// <summary>
    /// Creates an exception for a filter property the resource does not declare.
    /// </summary>
    /// <param name="propertyName">The filter property that could not be resolved.</param>
    /// <returns>The exception describing the unresolved property.</returns>
    public static UnknownFilterFieldException ForProperty(string propertyName)
        => new($"Unknown field '{propertyName}' in filter expression.")
        {
            PropertyName = propertyName
        };
}
