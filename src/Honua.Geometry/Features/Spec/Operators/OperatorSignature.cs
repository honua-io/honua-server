// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Spec.Domain;

namespace Honua.Core.Features.Spec.Operators;

/// <summary>
/// Typed description of a compute operator — its expected inputs, typed
/// parameters, and output type. Used by the type checker to validate compute
/// calls against the registered operator catalog.
/// </summary>
/// <param name="Name">Operator identifier (e.g. <c>spatial_join</c>).</param>
/// <param name="Inputs">Expected input ports by name.</param>
/// <param name="Parameters">Expected parameters by name.</param>
/// <param name="Output">Output type. If <see cref="SpecTypeKind.Unknown"/>, the checker uses the first input's type.</param>
/// <param name="CrsSensitive">When <c>true</c>, the semantic checker enforces projected-CRS on any <see cref="SpecTypeKind.Distance"/> or <see cref="SpecTypeKind.Area"/> parameter.</param>
public sealed record OperatorSignature(
    string Name,
    ImmutableArray<OperatorPort> Inputs,
    ImmutableArray<OperatorPort> Parameters,
    TypeRef Output,
    bool CrsSensitive = false)
{
    /// <summary>
    /// Looks up a named parameter descriptor. Returns <c>null</c> when the
    /// operator does not define the parameter.
    /// </summary>
    /// <param name="name">Parameter name.</param>
    /// <returns>Parameter descriptor or <c>null</c>.</returns>
    public OperatorPort? FindParameter(string name)
    {
        foreach (var param in Parameters)
        {
            if (string.Equals(param.Name, name, StringComparison.Ordinal))
            {
                return param;
            }
        }

        return null;
    }

    /// <summary>
    /// Looks up a named input descriptor.
    /// </summary>
    /// <param name="name">Input name.</param>
    /// <returns>Port descriptor or <c>null</c>.</returns>
    public OperatorPort? FindInput(string name)
    {
        foreach (var input in Inputs)
        {
            if (string.Equals(input.Name, name, StringComparison.Ordinal))
            {
                return input;
            }
        }

        return null;
    }
}

/// <summary>
/// Single input/parameter descriptor for an <see cref="OperatorSignature"/>.
/// </summary>
/// <param name="Name">Port name.</param>
/// <param name="Type">Expected type.</param>
/// <param name="Required">Whether validation must flag a missing value as an error.</param>
public sealed record OperatorPort(string Name, TypeRef Type, bool Required = true);
