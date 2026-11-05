// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Provisioner.BuildJobs;

public sealed record GeocoderBuildRequest
{
    public required string SourceId { get; init; }

    public required string ProductId { get; init; }

    public required ProvisionerArea Area { get; init; }

    public required string FeedstockTable { get; init; }

    public string SchemaName { get; init; } = "public";

    public required string ArtifactName { get; init; }

    public required string ArtifactKey { get; init; }

    public string LocatorKind { get; init; } = GeocoderArtifactKinds.NominatimPbf;
}

public sealed record RouterBuildRequest
{
    public required string SourceId { get; init; }

    public required string ProductId { get; init; }

    public required ProvisionerArea Area { get; init; }

    public required string FeedstockTable { get; init; }

    public string SchemaName { get; init; } = "public";

    public required string ArtifactName { get; init; }

    public required string ArtifactKey { get; init; }

    public string GraphKind { get; init; } = RouterArtifactKinds.PgRoutingTopology;
}
