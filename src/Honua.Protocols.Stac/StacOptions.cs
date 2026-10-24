// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Protocols.Stac;

internal sealed class StacOptions
{
    public const string SectionName = "Stac";

    public StacNumberMatchedPolicy NumberMatchedPolicy { get; set; } = StacNumberMatchedPolicy.Exact;
}

internal enum StacNumberMatchedPolicy
{
    Exact = 0,
    OmitWhenExpensive = 1
}
