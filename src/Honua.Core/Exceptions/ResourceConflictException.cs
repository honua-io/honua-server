// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Exceptions;

public sealed class ResourceConflictException : InvalidOperationException
{
    public ResourceConflictException()
    {
    }

    public ResourceConflictException(string message)
        : base(message)
    {
    }

    public ResourceConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
