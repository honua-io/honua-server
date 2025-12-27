// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Exceptions;

/// <summary>
/// Exception thrown when a request conflicts with existing state.
/// </summary>
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
