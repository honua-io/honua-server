// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Provisioner.BuildJobs;

public static class ProvisionerBuildJobParameterKeys
{
    public const string SourceId = "provisioner.source_id";

    public const string ProductId = "provisioner.product_id";

    public const string Area = "provisioner.area";

    public const string FeedstockTable = "provisioner.feedstock_table";

    public const string SchemaName = "provisioner.schema_name";

    public const string ArtifactName = "provisioner.artifact_name";

    public const string ArtifactKey = "provisioner.artifact_key";

    public const string LocatorKind = "provisioner.locator_kind";

    public const string GraphKind = "provisioner.graph_kind";
}

public static class GeocoderArtifactKinds
{
    public const string NominatimPbf = "nominatim-pbf";
}

public static class RouterArtifactKinds
{
    public const string PgRoutingTopology = "pgrouting-topology";
}
