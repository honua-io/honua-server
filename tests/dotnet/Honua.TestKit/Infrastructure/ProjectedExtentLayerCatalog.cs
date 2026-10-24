// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.TestKit.Infrastructure;

/// <summary>
/// Shared identifiers for the projected-extent metadata test fixture. Tests seed the
/// Metadata v2 graph with these ids/SRID to validate projected extent fallback paths.
/// </summary>
public static class ProjectedExtentLayerCatalog
{
    public const string ServiceId = "projected-metadata";
    public const int LayerId = 2001;
    public const int LayerSrid = 26910;
}
