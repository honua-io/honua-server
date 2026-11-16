// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Shared.Models;

namespace Honua.Db.Databricks;

public sealed class DatabricksOptions
{
    public bool Enabled { get; set; } = true;

    public string Host { get; set; } = string.Empty;

    public string WarehouseId { get; set; } = string.Empty;

    public string Token { get; set; } = string.Empty;

    public string? Catalog { get; set; }

    public string? Schema { get; set; }

    public int CommandTimeoutSeconds { get; set; } = 120;

    public int PollIntervalMilliseconds { get; set; } = 750;

    public int MaxRetryAttempts { get; set; } = 3;

    public int RetryBaseDelayMilliseconds { get; set; } = 500;

    public DatabricksLayerOptions[] Layers { get; set; } = [];
}

public sealed class DatabricksLayerOptions
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string Table { get; set; } = string.Empty;

    public string? Catalog { get; set; }

    public string? Schema { get; set; }

    public string GeometryColumn { get; set; } = "geom";

    public string PrimaryKeyColumn { get; set; } = "id";

    public int Srid { get; set; } = SpatialConstants.DefaultSrid;

    public string GeometryType { get; set; } = "Point";

    public string[] Attributes { get; set; } = [];
}
