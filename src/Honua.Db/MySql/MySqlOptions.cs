// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Shared.Models;

namespace Honua.Db.MySql;

public sealed class MySqlOptions
{
    public string ConnectionString { get; set; } = string.Empty;

    public string EngineFlavor { get; set; } = nameof(Honua.Db.MySql.MySqlEngineFlavor.Mysql);

    public MySqlLayerOptions[] Layers { get; set; } = [];

    public MySqlServiceOptions[] Services { get; set; } = [];
}

public sealed class MySqlLayerOptions
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string Table { get; set; } = string.Empty;

    public string? Schema { get; set; }

    public string GeometryColumn { get; set; } = "geom";

    public string PrimaryKeyColumn { get; set; } = "id";

    public int Srid { get; set; } = SpatialConstants.DefaultSrid;

    public string GeometryType { get; set; } = "Point";

    public string[] Attributes { get; set; } = [];

    public Dictionary<string, string> AttributeTypes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class MySqlServiceOptions
{
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public int[] LayerIds { get; set; } = [];

    public string[] Capabilities { get; set; } = ["Query"];

    public string[]? EnabledProtocols { get; set; }
}
