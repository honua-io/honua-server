// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;

namespace Honua.MySql.Features.Infrastructure;

/// <summary>
/// Single source of truth for the "operation not supported" phrasing shared by the
/// MySQL/MariaDB query builder and data-access unsupported paths, so the two families
/// cannot drift out of sync.
/// </summary>
internal static class MySqlUnsupportedOperation
{
    /// <summary>Builds the standard "operation not supported" message for the given operation name.</summary>
    public static string Message(string operation)
        => string.Format(
            CultureInfo.InvariantCulture,
            "Operation '{0}' is not supported by the MySQL/MariaDB provider.",
            operation);

    /// <summary>Creates the standard <see cref="NotSupportedException"/> for the given operation name.</summary>
    public static NotSupportedException Create(string operation)
        => new(Message(operation));
}
