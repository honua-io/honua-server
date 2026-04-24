// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Protocols.GeoServices.GPServer.Models;

/// <summary>
/// GPServer service root response listing available tasks.
/// </summary>
internal sealed class GPServiceInfoResponse
{
    /// <summary>Service version.</summary>
    public double CurrentVersion { get; set; } = 10.81;

    /// <summary>Service description.</summary>
    public string? ServiceDescription { get; set; }

    /// <summary>List of GP task names.</summary>
    public string[]? Tasks { get; set; }

    /// <summary>Execution type for the service.</summary>
    public string? ExecutionType { get; set; }

    /// <summary>Service capabilities.</summary>
    public string? Capabilities { get; set; }

    /// <summary>Result map server name.</summary>
    public string? ResultMapServerName { get; set; }

    /// <summary>Maximum number of records returned.</summary>
    public int MaximumRecords { get; set; } = 1000;
}

/// <summary>
/// GP task metadata including parameters.
/// </summary>
internal sealed class GPTaskInfoResponse
{
    /// <summary>Task name.</summary>
    public string? Name { get; set; }

    /// <summary>Task display name.</summary>
    public string? DisplayName { get; set; }

    /// <summary>Task description.</summary>
    public string? Description { get; set; }

    /// <summary>Category of the GP tool.</summary>
    public string? Category { get; set; }

    /// <summary>Help URL.</summary>
    public string? HelpUrl { get; set; }

    /// <summary>Execution type: esriExecutionTypeAsynchronous or esriExecutionTypeSynchronous.</summary>
    public string? ExecutionType { get; set; }

    /// <summary>GP parameters.</summary>
    public GPParameterInfo[]? Parameters { get; set; }
}

/// <summary>
/// Describes a GP task parameter.
/// </summary>
internal sealed class GPParameterInfo
{
    /// <summary>Parameter name.</summary>
    public string? Name { get; set; }

    /// <summary>Data type (GPString, GPLong, GPDouble, GPBoolean, GPFeatureRecordSetLayer, etc.).</summary>
    public string? DataType { get; set; }

    /// <summary>Display name.</summary>
    public string? DisplayName { get; set; }

    /// <summary>Parameter description.</summary>
    public string? Description { get; set; }

    /// <summary>Parameter direction (esriGPParameterDirectionInput or esriGPParameterDirectionOutput).</summary>
    public string? Direction { get; set; }

    /// <summary>Default value.</summary>
    public string? DefaultValue { get; set; }

    /// <summary>Parameter type (esriGPParameterTypeRequired, esriGPParameterTypeOptional).</summary>
    public string? ParameterType { get; set; }

    /// <summary>Choice list.</summary>
    public string[]? ChoiceList { get; set; }
}

/// <summary>
/// Response for submitJob.
/// </summary>
internal sealed class GPSubmitJobResponse
{
    /// <summary>Job identifier.</summary>
    public string? JobId { get; set; }

    /// <summary>Esri job status string.</summary>
    public string? JobStatus { get; set; }
}

/// <summary>
/// Response for job status polling.
/// </summary>
internal sealed class GPJobStatusResponse
{
    /// <summary>Job identifier.</summary>
    public string? JobId { get; set; }

    /// <summary>Esri job status string.</summary>
    public string? JobStatus { get; set; }

    /// <summary>Result parameter references when job succeeded.</summary>
    public Dictionary<string, GPJobResultRef>? Results { get; set; }

    /// <summary>Job messages (informational, warning, error).</summary>
    public GPJobMessage[]? Messages { get; set; }
}

/// <summary>
/// Reference to a result parameter, included in job status on success.
/// </summary>
internal sealed class GPJobResultRef
{
    /// <summary>Relative URL to the result resource.</summary>
    public string? ParamUrl { get; set; }
}

/// <summary>
/// Job message entry (informational, warning, error).
/// </summary>
internal sealed class GPJobMessage
{
    /// <summary>Message type (esriJobMessageTypeInformative, esriJobMessageTypeWarning, esriJobMessageTypeError).</summary>
    public string? Type { get; set; }

    /// <summary>Message description.</summary>
    public string? Description { get; set; }
}

/// <summary>
/// Per-output result response.
/// </summary>
internal sealed class GPResultResponse
{
    /// <summary>Output parameter name.</summary>
    public string? ParamName { get; set; }

    /// <summary>Esri GP data type.</summary>
    public string? DataType { get; set; }

    /// <summary>Result value.</summary>
    public object? Value { get; set; }
}

/// <summary>
/// Synchronous execute response envelope.
/// </summary>
internal sealed class GPExecuteResponse
{
    /// <summary>Result parameters.</summary>
    public GPResultResponse[]? Results { get; set; }

    /// <summary>Messages.</summary>
    public GPJobMessage[]? Messages { get; set; }
}
