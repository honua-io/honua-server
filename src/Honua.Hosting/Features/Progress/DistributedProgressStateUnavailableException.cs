// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Progress;

// Callers must retry and must not treat this as proof that the tracked operation failed.
public sealed class DistributedProgressStateUnavailableException : InvalidOperationException
{
    public DistributedProgressStateUnavailableException()
    {
    }

    public DistributedProgressStateUnavailableException(string message)
        : base(message)
    {
    }

    public DistributedProgressStateUnavailableException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
