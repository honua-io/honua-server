// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Protocols.GeoServices.FeatureServer;

internal static partial class FeatureServerEndpoints
{
    private sealed partial class CalcSqlExpressionParser
    {
        // Decimal arithmetic always throws on overflow; report it as an expression error instead of a 500.
        private static decimal? ComputeWithinRange(TokenKind operation, decimal left, decimal right)
        {
            try
            {
                return operation switch
                {
                    TokenKind.Plus => left + right,
                    TokenKind.Minus => left - right,
                    TokenKind.Star => left * right,
                    TokenKind.Slash => left / right,
                    TokenKind.Percent => left % right,
                    _ => throw new ArgumentOutOfRangeException(nameof(operation))
                };
            }
            catch (OverflowException)
            {
                return null;
            }
        }
    }
}
