// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Queries.Filters.Cql2.Parsers;

/// <summary>
/// Utility class for identifying predicate types in CQL2 expressions.
/// Extracted from Cql2Parser to improve maintainability and focus.
/// </summary>
internal static class PredicateParser
{
    /// <summary>
    /// Determines if token type represents a spatial predicate
    /// </summary>
    public static bool IsSpatialPredicate(Cql2TokenType type)
    {
        return type is Cql2TokenType.S_Intersects or Cql2TokenType.S_Contains or
               Cql2TokenType.S_Within or Cql2TokenType.S_Crosses or
               Cql2TokenType.S_Touches or Cql2TokenType.S_Overlaps or
               Cql2TokenType.S_Disjoint or Cql2TokenType.S_Equals or
               Cql2TokenType.S_DWithin or Cql2TokenType.S_Beyond;
    }

    /// <summary>
    /// Determines if token type represents a temporal predicate
    /// </summary>
    public static bool IsTemporalPredicate(Cql2TokenType type)
    {
        return type is Cql2TokenType.T_After or Cql2TokenType.T_Before or
               Cql2TokenType.T_Contains or Cql2TokenType.T_Disjoint or
               Cql2TokenType.T_During or Cql2TokenType.T_Equals or
               Cql2TokenType.T_FinishedBy or Cql2TokenType.T_Finishes or
               Cql2TokenType.T_Intersects or Cql2TokenType.T_Meets or
               Cql2TokenType.T_MetBy or Cql2TokenType.T_OverlappedBy or
               Cql2TokenType.T_Overlaps or Cql2TokenType.T_StartedBy or
               Cql2TokenType.T_Starts;
    }

    /// <summary>
    /// Determines if token type represents an array predicate
    /// </summary>
    public static bool IsArrayPredicate(Cql2TokenType type)
    {
        return type is Cql2TokenType.A_Equals or Cql2TokenType.A_Contains or
               Cql2TokenType.A_ContainedBy or Cql2TokenType.A_Overlaps;
    }

    /// <summary>
    /// Gets the spatial operator for a given token type
    /// </summary>
    public static SpatialOperator GetSpatialOperator(Cql2TokenType type)
    {
        return type switch
        {
            Cql2TokenType.S_Intersects => SpatialOperator.Intersects,
            Cql2TokenType.S_Contains => SpatialOperator.Contains,
            Cql2TokenType.S_Within => SpatialOperator.Within,
            Cql2TokenType.S_Crosses => SpatialOperator.Crosses,
            Cql2TokenType.S_Touches => SpatialOperator.Touches,
            Cql2TokenType.S_Overlaps => SpatialOperator.Overlaps,
            Cql2TokenType.S_Disjoint => SpatialOperator.Disjoint,
            Cql2TokenType.S_Equals => SpatialOperator.Equals,
            Cql2TokenType.S_DWithin => SpatialOperator.DWithin,
            Cql2TokenType.S_Beyond => SpatialOperator.Beyond,
            _ => throw new ArgumentException($"Unknown spatial operator: {type}")
        };
    }

    /// <summary>
    /// Gets the temporal operator for a given token type
    /// </summary>
    public static TemporalOperator GetTemporalOperator(Cql2TokenType type)
    {
        return type switch
        {
            Cql2TokenType.T_After => TemporalOperator.After,
            Cql2TokenType.T_Before => TemporalOperator.Before,
            Cql2TokenType.T_Contains => TemporalOperator.Contains,
            Cql2TokenType.T_Disjoint => TemporalOperator.Disjoint,
            Cql2TokenType.T_During => TemporalOperator.During,
            Cql2TokenType.T_Equals => TemporalOperator.Equals,
            Cql2TokenType.T_FinishedBy => TemporalOperator.FinishedBy,
            Cql2TokenType.T_Finishes => TemporalOperator.Finishes,
            Cql2TokenType.T_Intersects => TemporalOperator.Intersects,
            Cql2TokenType.T_Meets => TemporalOperator.Meets,
            Cql2TokenType.T_MetBy => TemporalOperator.MetBy,
            Cql2TokenType.T_OverlappedBy => TemporalOperator.OverlappedBy,
            Cql2TokenType.T_Overlaps => TemporalOperator.Overlaps,
            Cql2TokenType.T_StartedBy => TemporalOperator.StartedBy,
            Cql2TokenType.T_Starts => TemporalOperator.Starts,
            _ => throw new ArgumentException($"Unknown temporal operator: {type}")
        };
    }

    /// <summary>
    /// Gets the array operator for a given token type
    /// </summary>
    public static ArrayOperator GetArrayOperator(Cql2TokenType type)
    {
        return type switch
        {
            Cql2TokenType.A_Equals => ArrayOperator.Equals,
            Cql2TokenType.A_Contains => ArrayOperator.Contains,
            Cql2TokenType.A_ContainedBy => ArrayOperator.ContainedBy,
            Cql2TokenType.A_Overlaps => ArrayOperator.Overlaps,
            _ => throw new ArgumentException($"Unknown array operator: {type}")
        };
    }

    /// <summary>
    /// Determines if the current token starts a function call
    /// </summary>
    public static bool IsFunctionStart(IReadOnlyList<Cql2Token> tokens, int position)
    {
        return position + 1 < tokens.Count &&
               (tokens[position].Type is Cql2TokenType.Casei or Cql2TokenType.Accenti) &&
               tokens[position + 1].Type == Cql2TokenType.LeftParen;
    }

    /// <summary>
    /// Determines if the token type is a keyword identifier
    /// </summary>
    public static bool IsKeywordIdentifier(Cql2TokenType type)
    {
        return type is Cql2TokenType.Date or Cql2TokenType.Timestamp or Cql2TokenType.Interval or
            Cql2TokenType.Casei or Cql2TokenType.Accenti;
    }
}
