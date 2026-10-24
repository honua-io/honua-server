// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Progress;

/// <summary>
/// The shared (Redis) progress state could not be read or written and the store is not allowed to
/// answer from local memory. The condition is transient: callers must retry, and must not treat it as
/// proof that the operation they track has failed.
/// </summary>
public sealed class DistributedProgressStateUnavailableException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    public DistributedProgressStateUnavailableException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">What was being attempted.</param>
    public DistributedProgressStateUnavailableException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and the underlying failure.</summary>
    /// <param name="message">What was being attempted.</param>
    /// <param name="innerException">The Redis failure.</param>
    public DistributedProgressStateUnavailableException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
