// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Capabilities.Models;

internal sealed record CapabilityManifestDataStores
{
    public required string[] InstalledProviders { get; init; }

    public string FullImageTagSuffix { get; } = "-full";

    public string DocumentationHref { get; } =
        "https://github.com/honua-io/honua-server/blob/trunk/docs/ops/nightly-images.md#data-store-variants";
}
