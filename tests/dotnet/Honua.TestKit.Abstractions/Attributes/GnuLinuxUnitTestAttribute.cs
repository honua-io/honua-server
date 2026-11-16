// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.TestKit.Constants;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Honua.TestKit.Attributes;

[TraitDiscoverer("Honua.TestKit.Attributes.GnuLinuxUnitTestDiscoverer", "Honua.TestKit.Abstractions")]
public sealed class GnuLinuxUnitTestAttribute : FactAttribute, ITraitAttribute
{
    public GnuLinuxUnitTestAttribute()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "Requires a GNU/Linux shell toolchain (bash plus coreutils); the PR gate runs this on ubuntu-latest.";
        }
    }
}

public sealed class GnuLinuxUnitTestDiscoverer : ITraitDiscoverer
{
    public IEnumerable<KeyValuePair<string, string>> GetTraits(IAttributeInfo traitAttribute)
    {
        return
        [
            new KeyValuePair<string, string>("Category", "Unit"),
            new KeyValuePair<string, string>("Tier", Tiers.Fast)
        ];
    }
}
