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

    public ValidationException()
    {
    }

    public ValidationException(string message)
        : base(message)
    {
    }

    public ValidationException(string message, IReadOnlyList<string> details)
        : base(message)
    {
        Details = details;
    }

    public ValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
