// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Server.Features.Geoprocessing;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Protocols.Ogc.Common;
using Honua.Server.Features.Protocols.Ogc.Api.Processes.Models;
using Honua.ServiceDefaults;

namespace Honua.Server.Features.Protocols.Ogc.Api.Processes;

/// <summary>
/// OGC API Processes process discovery and execution endpoints.
/// </summary>
internal static class ProcessEndpoints
{
    private const string BasePath = "/ogc/processes";
    private const string Tag = "OGC API Processes";

    // Canonical process representing the Honua geoprocessing runtime. The
    // first process-migration evidence slice also projects selected catalog
    // process ids individually, but the canonical plan surface remains
    // available for multi-step DAG submission.
    internal const string CanonicalProcessId = "honua-geoprocessing";

    private static readonly OgcProcessSummary CanonicalProcessSummary = new()
    {
        Id = CanonicalProcessId,
        Title = "Honua Geoprocessing",
        Description = "Executes an analysis plan through the Honua canonical geoprocessing runtime.",
        Version = "1.0.0",
        JobControlOptions = ImmutableArray.Create("async-execute"),
        OutputTransmission = ImmutableArray.Create("value")
    };

    // V1 canonical process declares no value-typed outputs. `/jobs/{id}/results` returns
    // `200 OK` with an empty document-mode body (OGC API Processes Part 1 §7.11.1) until
    // result storage is populated; the empty `Outputs` map keeps the published
    // description in sync with that contract.
    private static readonly OgcProcessDescription CanonicalProcessDescription = new()
    {
        Id = CanonicalProcessId,
        Title = "Honua Geoprocessing",
        Description = "Executes an analysis plan through the Honua canonical geoprocessing runtime. " +
                      "Accepts a plan specification with steps, inputs, and output expectations. " +
                      "Job status is available on the job endpoint; successful jobs return a " +
                      "document-mode results body (empty until the canonical process declares " +
                      "value-typed outputs).",
        Version = "1.0.0",
        JobControlOptions = ImmutableArray.Create("async-execute"),
        OutputTransmission = ImmutableArray.Create("value"),
        Inputs = ImmutableDictionary.CreateRange(new[]
        {
            KeyValuePair.Create("plan", new OgcProcessIoDescription
            {
                Title = "Analysis Plan",
                Description = "JSON-encoded analysis plan with steps, inputs, and output expectations.",
                Schema = new OgcProcessIoSchema { Type = "object", ContentMediaType = "application/json" }
            })
        }),
        Outputs = ImmutableDictionary<string, OgcProcessIoDescription>.Empty
    };

    public static void MapOgcProcessesProcessEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet($"{BasePath}/processes", GetProcessList)
            .WithTags(Tag)
            .WithName("OgcProcessesList")
            .WithSummary("List available processes")
            .Produces<OgcProcessList>()
            .ExcludeFromDescription();

        endpoints.MapGet($"{BasePath}/processes/{{processId}}", GetProcessDescription)
            .WithTags(Tag)
            .WithName("OgcProcessDescription")
            .WithSummary("Get process description")
            .Produces<OgcProcessDescription>()
            .Produces<OgcProcessError>(StatusCodes.Status404NotFound)
            .ExcludeFromDescription();

        // HANDLER-AUTHORIZED (#1144): the handler calls
        // IGeoprocessingJobService.EnsureCallerAuthorized for Process+Execute
        // before reading the body so unauthenticated callers get 401 ahead of
        // 400. Marked AllowAnonymous so the audit architecture guard records
        // the explicit decision.
        endpoints.MapPost($"{BasePath}/processes/{{processId}}/execution", ExecuteProcess)
            .WithTags(Tag)
            .WithName("OgcProcessExecute")
            .WithSummary("Execute a process")
            .Accepts<OgcExecuteRequest>(MediaTypes.Json)
            .Produces<OgcStatusInfo>(StatusCodes.Status201Created)
            .Produces<OgcProcessError>(StatusCodes.Status400BadRequest)
            .Produces<OgcProcessError>(StatusCodes.Status401Unauthorized)
            .Produces<OgcProcessError>(StatusCodes.Status403Forbidden)
            .Produces<OgcProcessError>(StatusCodes.Status404NotFound)
            .Produces<OgcProcessError>(StatusCodes.Status409Conflict)
            .Produces<OgcProcessError>(StatusCodes.Status501NotImplemented)
            .Produces<OgcProcessError>(StatusCodes.Status503ServiceUnavailable)
            .ExcludeFromDescription()
            .AllowAnonymous();
    }

    private static IResult GetProcessList(
        HttpContext context,
        ILogger<OgcProcessesEndpointsLog> logger,
        IProcessCatalog processCatalog)
    {
        EnrichActivity("GetProcessList");
        OgcProcessesLog.ProcessListRequested(logger);

        var baseUrl = BaseUrlResolver.GetBaseUrl(context);
        var summary = CanonicalProcessSummary with
        {
            Links = ImmutableArray.Create(
                Link.Create(
                    $"{baseUrl}{BasePath}/processes/{CanonicalProcessId}",
                    RelationTypes.Self,
                    MediaTypes.Json,
                    "Process description"))
        };

        var processBuilder = ImmutableArray.CreateBuilder<OgcProcessSummary>();
        processBuilder.Add(summary);

        foreach (var definition in processCatalog.ListProcesses()
                     .Where(ProcessMigrationEvidenceClassifier.IsFirstSliceOgcProcess)
                     .OrderBy(process => process.ProcessId, StringComparer.Ordinal))
        {
            processBuilder.Add(ToOgcProcessSummary(definition, baseUrl));
        }

        var processList = new OgcProcessList
        {
            Processes = processBuilder.ToImmutable(),
            Links = ImmutableArray.Create(
                Link.Create($"{baseUrl}{BasePath}/processes", RelationTypes.Self, MediaTypes.Json, "This document"))
        };

        return Results.Json(processList, OgcProcessesJsonContext.Default.OgcProcessList, MediaTypes.Json);
    }

    private static IResult GetProcessDescription(
        string processId,
        HttpContext context,
        ILogger<OgcProcessesEndpointsLog> logger,
        IProcessCatalog processCatalog)
    {
        EnrichActivity("GetProcess");
        OgcProcessesLog.ProcessDescriptionRequested(logger, processId);

        if (string.Equals(processId, CanonicalProcessId, StringComparison.OrdinalIgnoreCase))
        {
            var canonicalBaseUrl = BaseUrlResolver.GetBaseUrl(context);
            var description = CanonicalProcessDescription with
            {
                Links = ImmutableArray.Create(
                    Link.Create($"{canonicalBaseUrl}{BasePath}/processes/{CanonicalProcessId}", RelationTypes.Self, MediaTypes.Json, "This document"),
                    Link.Create($"{canonicalBaseUrl}{BasePath}/processes/{CanonicalProcessId}/execution", "http://www.opengis.net/def/rel/ogc/1.0/execute", MediaTypes.Json, "Execute process"))
            };

            return Results.Json(description, OgcProcessesJsonContext.Default.OgcProcessDescription, MediaTypes.Json);
        }

        var definition = processCatalog.GetProcess(processId);
        if (definition == null || !ProcessMigrationEvidenceClassifier.IsFirstSliceOgcProcess(definition))
        {
            OgcProcessesLog.ProcessNotFound(logger, processId);
            return OgcProcessesResults.NoSuchProcess(processId);
        }

        var baseUrl = BaseUrlResolver.GetBaseUrl(context);
        return Results.Json(
            ToOgcProcessDescription(definition, baseUrl),
            OgcProcessesJsonContext.Default.OgcProcessDescription,
            MediaTypes.Json);
    }

    private static async Task<IResult> ExecuteProcess(
        string processId,
        HttpContext context,
        ILogger<OgcProcessesEndpointsLog> logger,
        IGeoprocessingJobService jobService,
        IProcessCatalog processCatalog)
    {
        EnrichActivity("ExecuteProcess");

        var preferAsync = context.Request.Headers.TryGetValue("Prefer", out var preferValues)
            && preferValues.Any(v => v != null && v.Contains("respond-async", StringComparison.OrdinalIgnoreCase));

        try
        {
            jobService.EnsureCallerAuthorized(
                context.User,
                OperatorResourceType.Process,
                OperatorOperation.Execute);

            var definition = string.Equals(processId, CanonicalProcessId, StringComparison.OrdinalIgnoreCase)
                ? null
                : processCatalog.GetProcess(processId);
            if (definition != null && !ProcessMigrationEvidenceClassifier.IsFirstSliceOgcProcess(definition))
            {
                definition = null;
            }

            if (!string.Equals(processId, CanonicalProcessId, StringComparison.OrdinalIgnoreCase)
                && definition == null)
            {
                OgcProcessesLog.ProcessNotFound(logger, processId);
                return OgcProcessesResults.NoSuchProcess(processId);
            }

            OgcExecuteRequest? request;
            try
            {
                request = await JsonSerializer.DeserializeAsync(
                    context.Request.Body,
                    OgcProcessesJsonContext.Default.OgcExecuteRequest,
                    context.RequestAborted)
                    .ConfigureAwait(false);
            }
            catch (JsonException)
            {
                OgcProcessesLog.ExecutionRequestInvalid(logger, processId);
                return OgcProcessesResults.Error(
                    StatusCodes.Status400BadRequest,
                    "Invalid execution request",
                    "Request body must be valid JSON.");
            }

            if (request == null)
            {
                OgcProcessesLog.ExecutionRequestInvalid(logger, processId);
                return OgcProcessesResults.Error(
                    StatusCodes.Status400BadRequest,
                    "Invalid execution request",
                    "Request body is required.");
            }

            if (request.Response != null
                && !string.Equals(request.Response, "document", StringComparison.OrdinalIgnoreCase))
            {
                OgcProcessesLog.UnsupportedResponseMode(logger, processId, request.Response);
                return OgcProcessesResults.Error(
                    StatusCodes.Status501NotImplemented,
                    "Unsupported response mode",
                    $"Response mode '{request.Response}' is not supported. V1 only supports 'document' mode.");
            }

            if (!TryBuildAnalysisPlan(processId, request, definition, out var analysisPlan, out var parseError))
            {
                OgcProcessesLog.PlanStructureInvalid(logger, processId, parseError ?? "Unknown plan parsing error.");
                return OgcProcessesResults.Error(
                    StatusCodes.Status400BadRequest,
                    "Invalid analysis plan",
                    parseError ?? "The analysis plan payload is invalid.");
            }

            OgcProcessesLog.ExecutionRequested(logger, processId, true);

            var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["submittedVia"] = "OGC-API-Processes",
                ["protocolProcessId"] = processId
            };
            if (definition != null)
            {
                AddOutputBindings(metadata, definition);
            }

            var jobRecord = await jobService
                .SubmitJobAsync(
                    analysisPlan!,
                    idempotencyKey: null,
                    context.User,
                    metadata,
                    context.RequestAborted)
                .ConfigureAwait(false);

            OgcProcessesLog.JobCreated(logger, jobRecord.OperationId, processId);

            var baseUrl = BaseUrlResolver.GetBaseUrl(context);
            var statusInfo = OgcProcessesConversionHelpers.ToOgcStatusInfo(jobRecord, processId, baseUrl);

            context.Response.Headers.Location = $"{baseUrl}{BasePath}/jobs/{jobRecord.OperationId}";
            if (preferAsync)
            {
                context.Response.Headers["Preference-Applied"] = "respond-async";
            }

            return Results.Json(
                statusInfo,
                OgcProcessesJsonContext.Default.OgcStatusInfo,
                MediaTypes.Json,
                StatusCodes.Status201Created);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (GeoprocessingAuthorizationException authEx)
        {
            OgcProcessesLog.AuthorizationDenied(
                logger,
                OperatorResourceType.Process.ToString(),
                OperatorOperation.Execute.ToString());
            return FormatOgcAuthError(authEx.RequiresAuthentication);
        }
        catch (GeoprocessingApprovalRequiredException approvalEx)
        {
            OgcProcessesLog.ExecutionRejectedApprovalRequired(logger, approvalEx.PolicyRef);
            return FormatOgcApprovalError(approvalEx.PolicyRef, approvalEx.Message);
        }
        catch (GeoprocessingValidationException validationEx)
        {
            OgcProcessesLog.PlanStructureInvalid(logger, processId, validationEx.Message);
            return OgcProcessesResults.Error(
                StatusCodes.Status400BadRequest,
                "Invalid analysis plan",
                validationEx.Message);
        }
        catch (GeoprocessingStoreUnavailableException)
        {
            OgcProcessesLog.JobStoreUnavailable(logger);
            return OgcProcessesResults.StoreUnavailable();
        }
        catch (GeoprocessingAdmissionException admissionEx)
        {
            context.Response.Headers["Retry-After"] = admissionEx.RetryAfterSeconds.ToString();
            OgcProcessesLog.ExecutionRejectedByAdmission(
                logger,
                admissionEx.Outcome.ToString(),
                admissionEx.DenyingDimension.ToString(),
                admissionEx.PolicyRef);
            return OgcProcessesResults.Error(
                StatusCodes.Status503ServiceUnavailable,
                "Service unavailable",
                admissionEx.Message);
        }
        catch (GeoprocessingIdempotencyConflictException conflictEx)
        {
            return OgcProcessesResults.Error(
                StatusCodes.Status409Conflict,
                "Conflict",
                conflictEx.Message);
        }
        catch (Exception ex)
        {
            OgcProcessesResults.RecordException(ex);
            return OgcProcessesResults.Error(
                StatusCodes.Status500InternalServerError,
                "Internal server error",
                "An error occurred while executing the process.");
        }
    }

    private static bool TryParseAnalysisPlan(
        JsonElement planElement,
        out AnalysisPlan? plan,
        out string? error)
    {
        plan = null;
        error = null;

        if (planElement.ValueKind != JsonValueKind.Object)
        {
            error = "The 'plan' input must be a JSON object.";
            return false;
        }

        if (!TryGetStringProperty(planElement, "planId", "plan_id", out var planId))
        {
            error = "The analysis plan must contain a string 'planId' property.";
            return false;
        }

        var steps = new List<AnalysisPlanStep>();
        if (planElement.TryGetProperty("steps", out var stepsElement))
        {
            if (stepsElement.ValueKind != JsonValueKind.Array)
            {
                error = "The analysis plan 'steps' property must be an array.";
                return false;
            }

            foreach (var stepElement in stepsElement.EnumerateArray())
            {
                if (!TryParseStep(stepElement, out var step, out error))
                {
                    return false;
                }

                steps.Add(step!);
            }
        }

        var outputs = new List<ArtifactKind>();
        if (planElement.TryGetProperty("outputs", out var outputsElement)
            && outputsElement.ValueKind != JsonValueKind.Null)
        {
            if (outputsElement.ValueKind != JsonValueKind.Array)
            {
                error = "The analysis plan 'outputs' property must be an array of artifact kind strings.";
                return false;
            }

            foreach (var outputElement in outputsElement.EnumerateArray())
            {
                var outputValue = outputElement.ValueKind == JsonValueKind.String
                    ? outputElement.GetString()
                    : null;
                if (!TryParseArtifactKind(outputValue, out var outputKind))
                {
                    error = $"Unsupported artifact kind '{outputValue}'.";
                    return false;
                }

                outputs.Add(outputKind);
            }
        }

        plan = new AnalysisPlan
        {
            PlanId = planId!,
            IntentId = "ogc-execute",
            Steps = steps,
            Outputs = outputs
        };
        return true;
    }

    private static bool TryBuildAnalysisPlan(
        string processId,
        OgcExecuteRequest request,
        ProcessDefinition? processDefinition,
        out AnalysisPlan? plan,
        out string? error)
    {
        plan = null;
        error = null;

        if (processDefinition == null)
        {
            if (request.Inputs == null
                || !request.Inputs.TryGetValue("plan", out var planElement)
                || planElement.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            {
                error = "The 'inputs' object must contain a 'plan' property with a non-null analysis plan.";
                return false;
            }

            return TryParseAnalysisPlan(planElement, out plan, out error);
        }

        if (request.Inputs == null)
        {
            error = "The 'inputs' object is required.";
            return false;
        }

        var inputs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var input in request.Inputs)
        {
            if (input.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            {
                error = $"Input '{input.Key}' must not be null.";
                return false;
            }

            inputs[input.Key] = JsonElementToCanonicalInput(input.Value);
        }

        var slug = processDefinition.ProcessId.Replace(".", "-", StringComparison.Ordinal);
        plan = new AnalysisPlan
        {
            PlanId = $"ogc-{slug}-{Guid.NewGuid():N}",
            IntentId = $"ogc-process:{processDefinition.ProcessId}",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = $"ogc-{slug}",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = processDefinition.ProcessId,
                    Inputs = inputs
                }
            ],
            Outputs = processDefinition.OutputArtifactKinds
        };

        return true;
    }

    private static string JsonElementToCanonicalInput(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => element.GetRawText(),
            _ => element.GetRawText()
        };

    private static OgcProcessSummary ToOgcProcessSummary(ProcessDefinition definition, string baseUrl)
        => new()
        {
            Id = definition.ProcessId,
            Title = definition.Title,
            Description = definition.Description,
            Version = "1.0.0",
            JobControlOptions = ImmutableArray.Create("async-execute"),
            OutputTransmission = ImmutableArray.Create("value"),
            Links = ImmutableArray.Create(
                Link.Create(
                    $"{baseUrl}{BasePath}/processes/{Uri.EscapeDataString(definition.ProcessId)}",
                    RelationTypes.Self,
                    MediaTypes.Json,
                    "Process description"))
        };

    private static OgcProcessDescription ToOgcProcessDescription(ProcessDefinition definition, string baseUrl)
        => new()
        {
            Id = definition.ProcessId,
            Title = definition.Title,
            Description = $"{definition.Description} First-slice migration evidence projection; execution is asynchronous and returns document-mode artifact references when the runtime publishes results.",
            Version = "1.0.0",
            JobControlOptions = ImmutableArray.Create("async-execute"),
            OutputTransmission = ImmutableArray.Create("value"),
            Inputs = definition.Parameters
                .ToImmutableDictionary(
                    parameter => parameter.Name,
                    ToOgcInputDescription,
                    StringComparer.Ordinal),
            Outputs = BuildOgcOutputDescriptions(definition),
            Links = ImmutableArray.Create(
                Link.Create(
                    $"{baseUrl}{BasePath}/processes/{Uri.EscapeDataString(definition.ProcessId)}",
                    RelationTypes.Self,
                    MediaTypes.Json,
                    "This document"),
                Link.Create(
                    $"{baseUrl}{BasePath}/processes/{Uri.EscapeDataString(definition.ProcessId)}/execution",
                    "http://www.opengis.net/def/rel/ogc/1.0/execute",
                    MediaTypes.Json,
                    "Execute process"))
        };

    private static OgcProcessIoDescription ToOgcInputDescription(ProcessParameterSpec parameter)
        => new()
        {
            Title = parameter.DisplayName,
            Description = parameter.Required
                ? $"{parameter.Description} Required."
                : parameter.Description,
            Schema = new OgcProcessIoSchema
            {
                Type = parameter.ValueType switch
                {
                    ProcessParameterValueType.WholeNumber or ProcessParameterValueType.Srid => "integer",
                    ProcessParameterValueType.FloatingPoint => "number",
                    ProcessParameterValueType.Flag => "boolean",
                    ProcessParameterValueType.WkbArray => "array",
                    _ => "string"
                },
                ContentMediaType = parameter.ValueType switch
                {
                    ProcessParameterValueType.Wkb => "application/wkb",
                    ProcessParameterValueType.WkbArray => "application/json",
                    _ => null
                }
            }
        };

    private static ImmutableDictionary<string, OgcProcessIoDescription> BuildOgcOutputDescriptions(
        ProcessDefinition definition)
    {
        var outputs = ImmutableDictionary.CreateBuilder<string, OgcProcessIoDescription>(StringComparer.Ordinal);
        for (var index = 0; index < definition.OutputArtifactKinds.Count; index++)
        {
            var kind = definition.OutputArtifactKinds[index];
            var name = BuildOutputName(kind, index, definition.OutputArtifactKinds);
            outputs[name] = new OgcProcessIoDescription
            {
                Title = name,
                Description = $"Artifact result of type {kind}.",
                Schema = new OgcProcessIoSchema { Type = "object", ContentMediaType = MediaTypes.Json }
            };
        }

        return outputs.ToImmutable();
    }

    private static void AddOutputBindings(
        Dictionary<string, string> metadata,
        ProcessDefinition definition)
    {
        for (var index = 0; index < definition.OutputArtifactKinds.Count; index++)
        {
            var outputName = BuildOutputName(
                definition.OutputArtifactKinds[index],
                index,
                definition.OutputArtifactKinds);
            metadata[$"{GeoprocessingProtocolMetadataKeys.OutputNamePrefix}{index}"] = outputName;
        }
    }

    private static string BuildOutputName(ArtifactKind kind, int index, IReadOnlyList<ArtifactKind> allKinds)
    {
        var baseName = kind switch
        {
            ArtifactKind.FeatureLayer => "outputFeatureLayer",
            ArtifactKind.Table => "outputTable",
            ArtifactKind.Raster => "outputRaster",
            ArtifactKind.File => "outputFile",
            ArtifactKind.Report => "outputReport",
            ArtifactKind.Map => "outputMap",
            ArtifactKind.Scalar => "outputScalar",
            ArtifactKind.AppBundle => "outputBundle",
            _ => "output"
        };

        if (allKinds.Count(candidate => candidate == kind) <= 1)
        {
            return baseName;
        }

        var ordinal = 0;
        for (var i = 0; i <= index; i++)
        {
            if (allKinds[i] == kind)
            {
                ordinal++;
            }
        }

        return $"{baseName}{ordinal}";
    }

    private static bool TryParseStep(
        JsonElement stepElement,
        out AnalysisPlanStep? step,
        out string? error)
    {
        step = null;
        error = null;

        if (stepElement.ValueKind != JsonValueKind.Object)
        {
            error = "Each step in the analysis plan must be a JSON object.";
            return false;
        }

        var stepId = TryGetStringProperty(stepElement, "stepId", "step_id", out var parsedStepId)
            ? parsedStepId ?? string.Empty
            : string.Empty;

        if (!TryGetStringProperty(stepElement, "kind", null, out var stepKindValue))
        {
            error = $"Step '{stepId}' is missing a 'kind' property.";
            return false;
        }

        if (!TryParseStepKind(stepKindValue, out var stepKind))
        {
            error = $"Step '{stepId}' has unsupported step kind '{stepKindValue}'.";
            return false;
        }

        var inputs = new Dictionary<string, string>(StringComparer.Ordinal);
        if (stepElement.TryGetProperty("inputs", out var inputsElement)
            && inputsElement.ValueKind != JsonValueKind.Null)
        {
            if (inputsElement.ValueKind != JsonValueKind.Object)
            {
                error = $"Step '{stepId}' inputs must be a JSON object.";
                return false;
            }

            foreach (var property in inputsElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    error = $"Step '{stepId}' input '{property.Name}' must be a string value.";
                    return false;
                }

                inputs[property.Name] = property.Value.GetString() ?? string.Empty;
            }
        }

        var dependsOn = new List<string>();
        if (stepElement.TryGetProperty("dependsOn", out var dependsOnElement)
            || stepElement.TryGetProperty("depends_on", out dependsOnElement))
        {
            if (dependsOnElement.ValueKind is not JsonValueKind.Array and not JsonValueKind.Null)
            {
                error = $"Step '{stepId}' dependsOn must be an array of step identifiers.";
                return false;
            }

            if (dependsOnElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var dependencyElement in dependsOnElement.EnumerateArray())
                {
                    if (dependencyElement.ValueKind != JsonValueKind.String)
                    {
                        error = $"Step '{stepId}' dependsOn values must be strings.";
                        return false;
                    }

                    dependsOn.Add(dependencyElement.GetString() ?? string.Empty);
                }
            }
        }

        TryGetStringProperty(stepElement, "processId", "process_id", out var stepProcessId);

        step = new AnalysisPlanStep
        {
            StepId = stepId,
            Kind = stepKind,
            ProcessId = stepProcessId,
            Inputs = inputs,
            DependsOn = dependsOn
        };
        return true;
    }

    private static bool TryParseStepKind(string? value, out AnalysisPlanStepKind kind)
    {
        if (string.Equals(value, "queryFeatures", StringComparison.OrdinalIgnoreCase))
        {
            kind = AnalysisPlanStepKind.QueryFeatures;
            return true;
        }

        if (string.Equals(value, "geoprocess", StringComparison.OrdinalIgnoreCase))
        {
            kind = AnalysisPlanStepKind.Geoprocess;
            return true;
        }

        if (string.Equals(value, "aggregate", StringComparison.OrdinalIgnoreCase))
        {
            kind = AnalysisPlanStepKind.Aggregate;
            return true;
        }

        if (string.Equals(value, "renderMap", StringComparison.OrdinalIgnoreCase))
        {
            kind = AnalysisPlanStepKind.RenderMap;
            return true;
        }

        if (string.Equals(value, "export", StringComparison.OrdinalIgnoreCase))
        {
            kind = AnalysisPlanStepKind.Export;
            return true;
        }

        kind = default;
        return false;
    }

    private static bool TryParseArtifactKind(string? value, out ArtifactKind kind)
    {
        if (string.Equals(value, "scalar", StringComparison.OrdinalIgnoreCase))
        {
            kind = ArtifactKind.Scalar;
            return true;
        }

        if (string.Equals(value, "featureLayer", StringComparison.OrdinalIgnoreCase))
        {
            kind = ArtifactKind.FeatureLayer;
            return true;
        }

        if (string.Equals(value, "table", StringComparison.OrdinalIgnoreCase))
        {
            kind = ArtifactKind.Table;
            return true;
        }

        if (string.Equals(value, "raster", StringComparison.OrdinalIgnoreCase))
        {
            kind = ArtifactKind.Raster;
            return true;
        }

        if (string.Equals(value, "file", StringComparison.OrdinalIgnoreCase))
        {
            kind = ArtifactKind.File;
            return true;
        }

        if (string.Equals(value, "report", StringComparison.OrdinalIgnoreCase))
        {
            kind = ArtifactKind.Report;
            return true;
        }

        if (string.Equals(value, "map", StringComparison.OrdinalIgnoreCase))
        {
            kind = ArtifactKind.Map;
            return true;
        }

        if (string.Equals(value, "appBundle", StringComparison.OrdinalIgnoreCase))
        {
            kind = ArtifactKind.AppBundle;
            return true;
        }

        kind = default;
        return false;
    }

    private static bool TryGetStringProperty(
        JsonElement element,
        string propertyName,
        string? alternatePropertyName,
        out string? value)
    {
        if (element.TryGetProperty(propertyName, out var property)
            || (alternatePropertyName != null && element.TryGetProperty(alternatePropertyName, out property)))
        {
            if (property.ValueKind == JsonValueKind.String)
            {
                value = property.GetString();
                return true;
            }
        }

        value = null;
        return false;
    }

    internal static IResult FormatOgcAuthError(Core.Features.Security.Abstractions.AccessDecision decision)
        => FormatOgcAuthError(decision.RequiresAuthentication);

    internal static IResult FormatOgcAuthError(bool requiresAuthentication)
    {
        if (requiresAuthentication)
        {
            return OgcProcessesResults.Error(
                StatusCodes.Status401Unauthorized,
                "Authentication required",
                "Authentication is required for this operation.");
        }

        return OgcProcessesResults.Error(
            StatusCodes.Status403Forbidden,
            "Permission denied",
            "You do not have permission to perform this operation.");
    }

    internal static IResult FormatOgcApprovalError(ApprovalRequirement approval)
        => FormatOgcApprovalError(
            approval.PolicyRef ?? "unknown",
            $"This operation requires approval (policy: {approval.PolicyRef}). Contact an administrator to request approval for this operation.");

    internal static IResult FormatOgcApprovalError(string policyRef, string? detail)
        => OgcProcessesResults.Error(
            StatusCodes.Status403Forbidden,
            "Approval required",
            string.IsNullOrWhiteSpace(detail)
                ? $"This operation requires approval (policy: {policyRef}). Contact an administrator to request approval for this operation."
                : detail);

    private static void EnrichActivity(string operation)
    {
        var activity = Activity.Current;
        if (activity == null)
        {
            return;
        }

        activity.SetTag(HonuaTelemetry.Tags.Protocol, "OGC-API-Processes");
        activity.SetTag(HonuaTelemetry.Tags.Operation, operation);
    }
}
