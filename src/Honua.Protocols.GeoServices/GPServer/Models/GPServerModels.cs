// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Protocols.GeoServices.GPServer.Models;

internal sealed class GPServiceInfoResponse
{
    // No ArcGIS Server version (currentVersion/fullVersion) is advertised. Honua is an
    // independent GeoServices REST server and must not impersonate a specific ArcGIS Server
    // release. Do NOT add a currentVersion/fullVersion field (guarded by NoGeoServicesServerVersionTests).

    public string? ServiceDescription { get; set; }

    public string[]? Tasks { get; set; }

    public string? ExecutionType { get; set; }

    public string? Capabilities { get; set; }

    public string? ResultMapServerName { get; set; }

    public int MaximumRecords { get; set; } = 1000;
}

internal sealed class GPTaskInfoResponse
{
    public string? Name { get; set; }

    public string? DisplayName { get; set; }

    public string? Description { get; set; }

    public string? Category { get; set; }

    public string? HelpUrl { get; set; }

    public string? ExecutionType { get; set; }

    public GPParameterInfo[]? Parameters { get; set; }
}

internal sealed class GPParameterInfo
{
    public string? Name { get; set; }

    public string? DataType { get; set; }

    public string? DisplayName { get; set; }

    public string? Description { get; set; }

    public string? Direction { get; set; }

    // arcgis.geoprocessing import_toolbox() raises KeyError('defaultValue') when the key is absent, so a null default is serialized.
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public JsonElement? DefaultValue { get; set; }

    public string? ParameterType { get; set; }

    public string[]? ChoiceList { get; set; }
}

internal sealed class GPSubmitJobResponse
{
    public string? JobId { get; set; }

    public string? JobStatus { get; set; }
}

internal sealed class GPJobStatusResponse
{
    public string? JobId { get; set; }

    public string? JobStatus { get; set; }

    public Dictionary<string, GPJobResultRef>? Results { get; set; }

    public GPJobMessage[]? Messages { get; set; }
}

internal sealed class GPJobResultRef
{
    public string? ParamUrl { get; set; }
}

internal sealed class GPJobMessage
{
    public string? Type { get; set; }

    public string? Description { get; set; }
}

internal sealed class GPResultResponse
{
    public string? ParamName { get; set; }

    public string? DataType { get; set; }

    public object? Value { get; set; }
}

internal sealed class GPJobsListResponse
{
    public List<GPJobListItem> Jobs { get; set; } = [];

    public string? NextCursor { get; set; }
}

internal sealed class GPJobListItem
{
    public string? JobId { get; set; }

    public string? JobStatus { get; set; }

    public long SubmissionTime { get; set; }

    public long LastUpdatedTime { get; set; }
}

internal sealed class GPExecuteResponse
{
    public GPResultResponse[]? Results { get; set; }

    public GPJobMessage[]? Messages { get; set; }

    public string? JobStatus { get; set; }
}
