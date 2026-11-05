// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Capabilities;

internal sealed class CapabilityManifestFeatureOptions
{
    public const string FromRegistryConfigKey = "Capabilities:ManifestFromRegistry";

    public bool FromRegistry { get; set; }
}
