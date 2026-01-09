// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Exceptions;

/// <summary>
/// Exception thrown when a service is temporarily unavailable.
/// </summary>
public sealed class ServiceUnavailableException : InvalidOperationException
{
    /// <summary>
    /// Optional retry-after hint in seconds.
    /// </summary>
    public int? RetryAfterSeconds { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="ServiceUnavailableException"/> class.
    /// </summary>
    public ServiceUnavailableException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ServiceUnavailableException"/> class with a specified error message.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    public ServiceUnavailableException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ServiceUnavailableException"/> class with a specified error message and retry-after hint.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="retryAfterSeconds">The number of seconds after which the client should retry the request.</param>
    public ServiceUnavailableException(string message, int retryAfterSeconds)
        : base(message)
    {
        RetryAfterSeconds = retryAfterSeconds;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ServiceUnavailableException"/> class with a specified error message and a reference to the inner exception that is the cause of this exception.
    /// </summary>
    /// <param name="message">The error message that explains the reason for the exception.</param>
    /// <param name="innerException">The exception that is the cause of the current exception.</param>
    public ServiceUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
