// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Protocols.Ogc.Classic.Wfs20;

internal sealed class Wfs20Options
{
    public const string SectionName = "Wfs20";

    /// <summary>
    /// Controls whether GetFeature computes an exact numberMatched value for finite page reads.
    /// </summary>
    public Wfs20NumberMatchedPolicy NumberMatchedPolicy { get; set; } = Wfs20NumberMatchedPolicy.Exact;
}

internal enum Wfs20NumberMatchedPolicy
{
    Exact = 0,
    UnknownWhenExpensive = 1
}
