// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Protocols.GeoServices.MapServer;

// A dynamic-layer workspace never accepts an arbitrary connection string, raw SQL, or an unpublished table; the table must already be a published resource in an allowed workspace, and that resource's access policy still applies.
public sealed class MapServerDynamicLayersOptions
{
    public const string SectionName = "GeoServices:MapServer:DynamicLayers";

    public bool WorkspaceLayersEnabled { get; set; }

    public bool AllowAllRegisteredWorkspaces { get; set; }

    public IList<string> AllowedWorkspaceIds { get; } = new List<string>();
}
