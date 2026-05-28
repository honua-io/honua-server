// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections;
using System.Reflection;
using Npgsql;

namespace Honua.Postgres.Features.Infrastructure.Session;

/// <summary>
/// Binds an opaque <c>parameters</c> object to an <see cref="NpgsqlCommand"/>
/// for the session abstraction. Supports:
/// <list type="bullet">
///   <item><description><c>null</c> — no parameters.</description></item>
///   <item><description><c>IReadOnlyDictionary&lt;string, object?&gt;</c> or any <see cref="IDictionary"/>.</description></item>
///   <item><description>Anonymous / POCO objects — public readable properties are read by reflection.</description></item>
/// </list>
/// </summary>
internal static class ParameterBinder
{
    public static void Bind(NpgsqlCommand command, object? parameters)
    {
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

        // Reflect public readable properties (anonymous / POCO objects).
        var properties = parameters.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public);
        foreach (var property in properties)
        {
            if (!property.CanRead)
            {
                continue;
            }

            var value = property.GetValue(parameters);
            AddParameter(command, property.Name, value);
        }
    }

    private static void AddParameter(NpgsqlCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
