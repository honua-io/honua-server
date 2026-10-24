// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Exceptions;
using Npgsql;

namespace Honua.Db.Postgres.Features.FeatureStore.Services;

/// <summary>
/// Turns a PostGIS failure to load a catalog grid into a client-safe error.
/// The pipeline is not retried as a two-argument transform.
/// </summary>
internal static class DatumGridException
{
    internal const string MissingGridMessage =
        "The datum catalog grid is not installed. The request failed instead of falling back to a null transform.";

    internal static bool IsMissingGrid(PostgresException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var message = exception.Message;
        if (string.IsNullOrEmpty(message))
        {
            return false;
        }

        return message.Contains("grid", StringComparison.OrdinalIgnoreCase)
            || message.Contains("hgridshift", StringComparison.OrdinalIgnoreCase)
            || message.Contains("datum shift", StringComparison.OrdinalIgnoreCase);
    }

    internal static ValidationException MissingGrid(PostgresException exception)
        => new(MissingGridMessage, exception);
}
