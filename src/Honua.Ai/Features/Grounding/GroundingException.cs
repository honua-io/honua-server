// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Grounding.Domain;

namespace Honua.Ai.Grounding;

internal sealed class GroundingException : Exception
{
    public GroundingErrorKind Kind { get; }

    public GroundingException(GroundingErrorKind kind, string message)
        : base(message)
    {
        Kind = kind;
    }

    public GroundingException(GroundingErrorKind kind, string message, Exception innerException)
        : base(message, innerException)
    {
        Kind = kind;
    }
}
