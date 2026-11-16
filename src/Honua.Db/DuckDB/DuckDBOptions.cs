// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Shared.Models;

namespace Honua.Db.DuckDB;

public sealed class DuckDBOptions
{
    public string DatabasePath { get; set; } = ":memory:";

    public string? SpatialExtensionPath { get; set; }

    public DuckDBHttpFsOptions HttpFs { get; set; } = new();

    public DuckDBAzureOptions Azure { get; set; } = new();

    public bool ReadOnly { get; set; } = true;

    public DuckDBLayerOptions[] Layers { get; set; } = [];

    public DuckDBServiceOptions[] Services { get; set; } = [];
}

public sealed class DuckDBLayerOptions
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string Table { get; set; } = string.Empty;

    public DuckDBExternalSourceOptions? ExternalSource { get; set; }

    public string GeometryColumn { get; set; } = "geom";

    public string ObjectIdColumn { get; set; } = "id";

    public int Srid { get; set; } = SpatialConstants.DefaultSrid;

    public string GeometryType { get; set; } = "Point";

    public string[]? Attributes { get; set; }
}

public sealed class DuckDBExternalSourceOptions
{
    public string Format { get; set; } = "GeoParquet";

    public string? Path { get; set; }

    public string[]? Paths { get; set; }

    public bool HivePartitioning { get; set; }

    public bool UnionByName { get; set; }
}

public sealed class DuckDBHttpFsOptions
{
    public DuckDBS3Options S3 { get; set; } = new();
}

public sealed class DuckDBS3Options
{
    public string? Region { get; set; }

    public string? Endpoint { get; set; }

    public string? AccessKeyId { get; set; }

    public string? SecretAccessKey { get; set; }

    public string? SessionToken { get; set; }

    public string? UrlStyle { get; set; }

    public bool? UseSsl { get; set; }

    public bool? RequesterPays { get; set; }
}

public sealed class DuckDBAzureOptions
{
    public string? ConnectionString { get; set; }

    public string? AccountName { get; set; }

    public string? Endpoint { get; set; }

    public string? CredentialChain { get; set; }
}

public sealed class DuckDBServiceOptions
{
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public int[] LayerIds { get; set; } = [];

    public string[] Capabilities { get; set; } = ["Query"];

    public string[]? EnabledProtocols { get; set; }
}
