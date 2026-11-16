// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Spec.Domain;

namespace Honua.Core.Features.Spec.Operators;

public sealed record OperatorSignature(
    string Name,
    ImmutableArray<OperatorPort> Inputs,
    ImmutableArray<OperatorPort> Parameters,
    TypeRef Output,
    bool CrsSensitive = false)
{
    public OperatorPort? FindParameter(string name) =>
        Parameters.FirstOrDefault(param => string.Equals(param.Name, name, StringComparison.Ordinal));

    public OperatorPort? FindInput(string name) =>
        Inputs.FirstOrDefault(input => string.Equals(input.Name, name, StringComparison.Ordinal));
}

public sealed record OperatorPort(string Name, TypeRef Type, bool Required = true);
