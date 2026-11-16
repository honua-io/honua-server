// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Xunit;

namespace Honua.TestKit.Attributes;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class ProtocolAttribute : Attribute
{
    public ProtocolAttribute(params string[] protocols)
    {
        Protocols = protocols ?? [];

        foreach (var protocol in Protocols)
        {
            Traits.Add(new TraitAttribute("Protocol", protocol));
        }
    }

    public IReadOnlyList<string> Protocols { get; }

    public string Protocol => Protocols.Count > 0 ? Protocols[0] : string.Empty;

    private List<TraitAttribute> Traits { get; } = [];
}
