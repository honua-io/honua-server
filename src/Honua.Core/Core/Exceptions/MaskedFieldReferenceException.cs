// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Exceptions;

// A query names a field the caller's field policy hides. This is a caller error (400), never
// a server fault, on every protocol surface.
public sealed class MaskedFieldReferenceException : ArgumentException
{
    public MaskedFieldReferenceException()
    {
    }

    public MaskedFieldReferenceException(string message)
        : base(message)
    {
    }

    public MaskedFieldReferenceException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public MaskedFieldReferenceException(string message, string paramName)
        : base(message, paramName)
    {
    }
}
