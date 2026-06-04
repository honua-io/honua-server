// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace Honua.Core.Features.Infrastructure.Session;

/// <summary>
/// Binds an opaque <c>parameters</c> object to a <see cref="DbCommand"/>
/// for the <see cref="Abstractions.IDatabaseSession"/> abstraction. Supports:
/// <list type="bullet">
///   <item><description><c>null</c> — no parameters.</description></item>
///   <item><description><c>IReadOnlyDictionary&lt;string, object?&gt;</c> or any <see cref="IDictionary"/>.</description></item>
///   <item><description>Anonymous / POCO objects — public readable properties are read by reflection.</description></item>
/// </list>
/// </summary>
public static class ParameterBinder
{
    /// <summary>
    /// Binds <paramref name="parameters"/> onto <paramref name="command"/>.
    /// </summary>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2075:'this' argument does not satisfy 'DynamicallyAccessedMemberTypes.PublicProperties'",
        Justification =
            "Reflection only reads the public instance property getters of the supplied parameters " +
            "object (dapper-style binding). Every caller of the IDatabaseSession abstraction passes " +
            "anonymous types, dictionaries, or POCOs that are constructed in-assembly, so the trimmer " +
            "preserves their public properties for the instances that reach this method. No properties " +
            "are discovered on types that are not otherwise instantiated, so binding stays trim-safe.")]
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

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
