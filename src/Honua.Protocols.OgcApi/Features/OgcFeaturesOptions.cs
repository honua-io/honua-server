// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Protocols.Ogc.Api.Features;

internal sealed class OgcFeaturesOptions
{
    public const string SectionName = "OgcFeatures";

    /// <summary>
    /// Controls whether the items endpoint computes an exact numberMatched value.
    /// </summary>
    public OgcFeaturesNumberMatchedPolicy NumberMatchedPolicy { get; set; } = OgcFeaturesNumberMatchedPolicy.Exact;

    /// <summary>
    /// Controls whether items responses include per-feature links.
    /// </summary>
    public bool IncludeFeatureLinks { get; set; } = true;
}

internal enum OgcFeaturesNumberMatchedPolicy
{
    Exact = 0,
    OmitWhenExpensive = 1
}
