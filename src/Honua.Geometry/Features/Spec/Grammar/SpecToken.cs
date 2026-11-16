// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Spec.Domain;

namespace Honua.Core.Features.Spec.Grammar;

#pragma warning disable CA1720 // Token names intentionally mirror the underlying literal kinds.
public enum SpecTokenType
{
    EndOfInput,

    Identifier,

    String,

    Number,

    Reference,

    LeftBrace,

    RightBrace,

    LeftBracket,

    RightBracket,

    LeftParen,

    RightParen,

    Equals,

    Comma,

    Dot,

    Unit,

    Boolean,

    Null
}
#pragma warning restore CA1720

public sealed record SpecToken(SpecTokenType Type, string Text, SourceSpan Span);
