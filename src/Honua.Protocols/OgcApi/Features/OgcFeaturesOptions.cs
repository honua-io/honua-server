// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Protocols.Ogc.Api.Features;

internal sealed class OgcFeaturesOptions
{
    public const string SectionName = "OgcFeatures";

    public OgcFeaturesNumberMatchedPolicy NumberMatchedPolicy { get; set; } = OgcFeaturesNumberMatchedPolicy.Exact;

    public bool IncludeFeatureLinks { get; set; } = true;
}

internal enum OgcFeaturesNumberMatchedPolicy
{
    Exact = 0,
    OmitWhenExpensive = 1
}
