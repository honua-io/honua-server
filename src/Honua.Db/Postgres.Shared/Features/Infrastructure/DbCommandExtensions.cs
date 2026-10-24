// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data.Common;

namespace Honua.Db.Postgres.Features.Infrastructure;

/// <summary>
/// Extension methods for DbCommand parameter management.
/// Centralizes the parameter-adding pattern used across Postgres stores.
/// </summary>
internal static class DbCommandExtensions
{
    /// <summary>
    /// Adds a named parameter to the command.
    /// </summary>
    public static void AddParameter(this DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
