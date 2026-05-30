// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data.Common;

namespace Honua.Postgres.Features.Infrastructure;

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
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
