// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Sdk.Admin.Exceptions;

/// <summary>
/// Exception thrown when a Honua Admin operation fails at the application level.
/// </summary>
public sealed class HonuaAdminOperationException : Exception
{
    /// <summary>
    /// The operation that failed.
    /// </summary>
    public string? Operation { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="HonuaAdminOperationException"/> class.
    /// </summary>
    /// <param name="message">A human-readable error message.</param>
    /// <param name="operation">The operation that failed.</param>
    public HonuaAdminOperationException(string message, string? operation = null)
        : base(message)
    {
        Operation = operation;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="HonuaAdminOperationException"/> class with an inner exception.
    /// </summary>
    /// <param name="message">A human-readable error message.</param>
    /// <param name="operation">The operation that failed.</param>
    /// <param name="innerException">The inner exception that caused this exception.</param>
    public HonuaAdminOperationException(string message, string? operation, Exception innerException)
        : base(message, innerException)
    {
        Operation = operation;
    }
}
