// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Spec.Domain;

namespace Honua.Core.Features.Spec.Grammar;

/// <summary>
/// Token types emitted by <see cref="SpecLexer"/>. The spec language is
/// brace-based and HCL/JSON-ish — it reuses keywords for its five top-level
/// sections, a small set of literal forms, and standard punctuation.
/// </summary>
#pragma warning disable CA1720 // Token names intentionally mirror the underlying literal kinds.
public enum SpecTokenType
{
    /// <summary>End-of-input sentinel.</summary>
    EndOfInput,

    /// <summary>Bareword identifier (<c>source</c>, <c>my_id</c>, <c>spatial_join</c>).</summary>
    Identifier,

    /// <summary>Double-quoted string literal.</summary>
    String,

    /// <summary>Numeric literal (integer, decimal, or scientific).</summary>
    Number,

    /// <summary>Reference token starting with <c>@</c>.</summary>
    Reference,

    /// <summary>Opening brace <c>{</c>.</summary>
    LeftBrace,

    /// <summary>Closing brace <c>}</c>.</summary>
    RightBrace,

    /// <summary>Opening bracket <c>[</c>.</summary>
    LeftBracket,

    /// <summary>Closing bracket <c>]</c>.</summary>
    RightBracket,

    /// <summary>Opening parenthesis <c>(</c>.</summary>
    LeftParen,

    /// <summary>Closing parenthesis <c>)</c>.</summary>
    RightParen,

    /// <summary>Equals sign <c>=</c>.</summary>
    Equals,

    /// <summary>Comma <c>,</c>.</summary>
    Comma,

    /// <summary>Dot <c>.</c>.</summary>
    Dot,

    /// <summary>Unit-annotation dot pair such as <c>.km</c> (emitted as <c>Dot</c> + <c>Identifier</c>; parser assembles the unit literal).</summary>
    Unit,

    /// <summary>Literal <c>true</c> or <c>false</c>.</summary>
    Boolean,

    /// <summary>Literal <c>null</c>.</summary>
    Null
}
#pragma warning restore CA1720

/// <summary>
/// Single lexical token with absolute offset, 1-based line/column, and
/// verbatim text (used by the parser and by diagnostic messages).
/// </summary>
/// <param name="Type">Token kind.</param>
/// <param name="Text">Raw token text (for strings, the unescaped value).</param>
/// <param name="Span">Source position + length.</param>
public sealed record SpecToken(SpecTokenType Type, string Text, SourceSpan Span);
