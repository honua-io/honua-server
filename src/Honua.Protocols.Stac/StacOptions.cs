// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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
