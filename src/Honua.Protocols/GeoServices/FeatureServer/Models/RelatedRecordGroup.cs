// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Protocols.GeoServices.FeatureServer.Models;

public sealed class RelatedRecordGroup
{
    public required long ObjectId { get; init; }

    // relatedRecords is always an array because the ArcGIS Maps SDK for JavaScript reads its length unconditionally, and it is omitted only for returnCountOnly, which carries a count instead.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GeoServicesFeature[]? RelatedRecords { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Count { get; init; }
}
