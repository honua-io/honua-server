// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Exceptions;

/// <summary>
/// Exception thrown when a request conflicts with existing state.
/// </summary>
public sealed class ResourceConflictException : InvalidOperationException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ResourceConflictException"/> class.
    /// </summary>
    public ResourceConflictException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ResourceConflictException"/> class with a specified error message.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    public ResourceConflictException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ResourceConflictException"/> class with a specified error message and a reference to the inner exception that is the cause of this exception.
    /// </summary>
    /// <param name="message">The error message that explains the reason for the exception.</param>
    /// <param name="innerException">The exception that is the cause of the current exception.</param>
    public ResourceConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
