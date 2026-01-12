// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Exceptions;

/// <summary>
/// Exception thrown when request validation fails.
/// Messages from this exception are considered safe to expose to clients.
/// </summary>
public sealed class ValidationException : InvalidOperationException
{
    /// <summary>
    /// Additional validation error details that are safe to expose.
    /// </summary>
    public IReadOnlyList<string>? Details { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="ValidationException"/> class.
    /// </summary>
    public ValidationException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ValidationException"/> class with a specified error message.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    public ValidationException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ValidationException"/> class with a specified error message and validation details.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="details">Additional validation error details that are safe to expose to clients.</param>
    public ValidationException(string message, IReadOnlyList<string> details)
        : base(message)
    {
        Details = details;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ValidationException"/> class with a specified error message and a reference to the inner exception that is the cause of this exception.
    /// </summary>
    /// <param name="message">The error message that explains the reason for the exception.</param>
    /// <param name="innerException">The exception that is the cause of the current exception.</param>
    public ValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
