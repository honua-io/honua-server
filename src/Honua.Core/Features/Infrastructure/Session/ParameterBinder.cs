// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections;
using System.Data.Common;

namespace Honua.Core.Features.Infrastructure.Session;

/// <summary>
/// Binds an opaque <c>parameters</c> object to a <see cref="DbCommand"/>
/// for the <see cref="Abstractions.IDatabaseSession"/> abstraction. Supports:
/// <list type="bullet">
///   <item><description><c>null</c> — no parameters.</description></item>
///   <item><description><c>IReadOnlyDictionary&lt;string, object?&gt;</c> or any <see cref="IDictionary"/>.</description></item>
/// </list>
/// </summary>
public static class ParameterBinder
{
    /// <summary>
    /// Binds <paramref name="parameters"/> onto <paramref name="command"/>.
    /// </summary>
    public static void Bind(DbCommand command, object? parameters)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (parameters is null)
        {
            return;
        }

        if (parameters is IReadOnlyDictionary<string, object?> typedDict)
        {
            foreach (var (name, value) in typedDict)
            {
                AddParameter(command, name, value);
            }
            return;
        }

        if (parameters is IDictionary dict)
        {
            foreach (DictionaryEntry entry in dict)
            {
                var name = entry.Key?.ToString();
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                AddParameter(command, name, entry.Value);
            }
            return;
        }

        throw new NotSupportedException(
            "Database session parameters must be provided as an IReadOnlyDictionary<string, object?> or IDictionary.");
    }

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
