// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Admin.Models;

public sealed class NetworkDatasetDto
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string EdgeTable { get; set; } = string.Empty;

    public string VertexTable { get; set; } = string.Empty;

    public int Srid { get; set; }

    public string Status { get; set; } = "active";

    public int TopologyVersion { get; set; }

    public bool NeedsRebuild { get; set; }

    public string? Description { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public string? CreatedBy { get; set; }

    public string? UpdatedBy { get; set; }
}

public sealed class RegisterNetworkDatasetRequest
{
    public string? Id { get; set; }

    public string? Name { get; set; }

    public string? EdgeTable { get; set; }

    public string? VertexTable { get; set; }

    public int? Srid { get; set; }

    public string? Status { get; set; }

    public string? Description { get; set; }
}

public sealed class UpdateNetworkDatasetRequest
{
    public string? Name { get; set; }

    public string? EdgeTable { get; set; }

    public string? VertexTable { get; set; }

    public int? Srid { get; set; }

    public string? Status { get; set; }

    public string? Description { get; set; }

    public bool? ClearDescription { get; set; }
}
