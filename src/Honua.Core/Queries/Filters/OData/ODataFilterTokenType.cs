// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Queries.Filters.OData;

internal enum ODataFilterTokenType
{
    EndOfInput,
    Identifier,
    StringLiteral,
    NumberLiteral,
    DateLiteral,
    DateTimeLiteral,
    BooleanLiteral,
    NullLiteral,
    LeftParen,
    RightParen,
    Comma,
    And,
    Or,
    Not,
    Eq,
    Ne,
    Gt,
    Ge,
    Lt,
    Le,
    In,
    Add,
    Sub,
    Mul,
    Div,
    Mod,
    Minus
}
