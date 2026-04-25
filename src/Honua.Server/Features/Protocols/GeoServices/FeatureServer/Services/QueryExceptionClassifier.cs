// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Npgsql;

namespace Honua.Server.Features.Protocols.GeoServices.FeatureServer.Services;

/// <summary>
/// Classifies database exceptions that should be treated as invalid client query syntax.
/// </summary>
internal static class QueryExceptionClassifier
{
    private static readonly HashSet<string> _invalidQuerySqlStates =
    [
        PostgresErrorCodes.SyntaxError,
        PostgresErrorCodes.InvalidParameterValue,
        PostgresErrorCodes.InvalidTextRepresentation,
        PostgresErrorCodes.UndefinedColumn,
        PostgresErrorCodes.UndefinedFunction
    ];

    public static bool IsInvalidQuerySyntax(PostgresException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return !string.IsNullOrWhiteSpace(exception.SqlState) &&
               _invalidQuerySqlStates.Contains(exception.SqlState);
    }
}
