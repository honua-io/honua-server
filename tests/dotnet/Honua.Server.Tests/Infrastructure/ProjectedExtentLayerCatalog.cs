// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Tests.Infrastructure;

/// <summary>
/// Shared identifiers for the projected-extent metadata test fixture. Tests seed the
/// Metadata v2 graph with these ids/SRID to validate projected extent fallback paths.
/// </summary>
internal static class ProjectedExtentLayerCatalog
{
    public const string ServiceId = "projected-metadata";
    public const int LayerId = 2001;
    public const int LayerSrid = 26910;
}
