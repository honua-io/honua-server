// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Xunit;

namespace Honua.TestKit.Attributes;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class OperationAttribute : Attribute
{
    public OperationAttribute(params string[] operations)
    {
        Operations = operations ?? [];

        foreach (var operation in Operations)
        {
            Traits.Add(new TraitAttribute("Operation", operation));
        }
    }

    public IReadOnlyList<string> Operations { get; }

    public string Operation => Operations.Count > 0 ? Operations[0] : string.Empty;

    private List<TraitAttribute> Traits { get; } = [];
}
