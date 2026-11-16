// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Honua.TestKit.Attributes;

[TraitDiscoverer("Honua.TestKit.Attributes.FlakyTestDiscoverer", "Honua.TestKit.Abstractions")]
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class FlakyTestAttribute : Attribute, ITraitAttribute
{
    public FlakyTestAttribute(string reason)
    {
        Reason = reason;
    }

    public string Reason { get; }
}

public sealed class FlakyTestDiscoverer : ITraitDiscoverer
{
    public IEnumerable<KeyValuePair<string, string>> GetTraits(IAttributeInfo traitAttribute)
    {
        return [new KeyValuePair<string, string>("Flaky", "true")];
    }
}
