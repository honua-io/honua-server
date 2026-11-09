// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Exceptions;

public sealed class ValidationException : InvalidOperationException
{
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
