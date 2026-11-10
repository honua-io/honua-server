// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Exceptions;

public sealed class UnknownFilterFieldException : ArgumentException
{
    public UnknownFilterFieldException()
    {
    }

    public UnknownFilterFieldException(string message)
        : base(message)
    {
    }

    public UnknownFilterFieldException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public string? PropertyName { get; private init; }

    public static UnknownFilterFieldException ForProperty(string propertyName)
        => new($"Unknown field '{propertyName}' in filter expression.")
        {
            PropertyName = propertyName
        };
}
