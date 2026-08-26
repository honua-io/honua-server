// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Geoprocessing.Raster;
using Honua.Geoprocessing;
using Honua.Infrastructure.Helpers;
using Honua.Protocols.Ogc.Common;
using Honua.Protocols.Ogc.Api.Processes.Models;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Honua.Protocols.Ogc.Api.Processes;

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
        JobControlOptions = ImmutableArray.Create("async-execute", "sync-execute"),
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
        JobControlOptions = ImmutableArray.Create("async-execute", "sync-execute"),
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
        // IGeoprocessingJobService.EnsureCallerAuthorizedAsync for Process+Execute
        // before reading the body so unauthenticated callers get 401 ahead of
        // 400. Marked AllowAnonymous so the audit architecture guard records
        // the explicit decision.
        endpoints.MapPost($"{BasePath}/processes/{{processId}}/execution", ExecuteProcess)
            .WithTags(Tag)
            .WithName("OgcProcessExecute")
            .WithSummary("Execute a process")
            .Accepts<OgcExecuteRequest>(MediaTypes.Json)
            .Produces<OgcResultsDocument>(StatusCodes.Status200OK)
            .Produces<OgcStatusInfo>(StatusCodes.Status201Created)
            .Produces<OgcProcessError>(StatusCodes.Status400BadRequest)
            .Produces<OgcProcessError>(StatusCodes.Status401Unauthorized)
            .Produces<OgcProcessError>(StatusCodes.Status403Forbidden)
            .Produces<OgcProcessError>(StatusCodes.Status404NotFound)
            .Produces<OgcProcessError>(StatusCodes.Status409Conflict)
            .Produces<OgcProcessError>(StatusCodes.Status408RequestTimeout)
            .Produces<OgcProcessError>(StatusCodes.Status500InternalServerError)
            .Produces<OgcProcessError>(StatusCodes.Status422UnprocessableEntity)
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
                     .Where(ProcessExecutionCapabilityCatalog.IsOgcCallable)
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
        if (definition == null || !ProcessExecutionCapabilityCatalog.IsOgcCallable(definition))
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
        IGeoprocessingJobTerminalService terminalService,
        IProcessCatalog processCatalog)
    {
        EnrichActivity("ExecuteProcess");
        var cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);

        // OGC API Processes Part 1 defines respond-async. Omission selects
        // synchronous execution when the catalog says the process supports it.
        var preferAsync = HasPreference(context.Request.Headers["Prefer"], "respond-async");

        try
        {
            await jobService.EnsureCallerAuthorizedAsync(
                context.User,
                OperatorResourceType.Process,
                OperatorOperation.Execute,
                cancellationToken).ConfigureAwait(false);

            var definition = string.Equals(processId, CanonicalProcessId, StringComparison.OrdinalIgnoreCase)
                ? null
                : processCatalog.GetProcess(processId);
            if (definition != null && !ProcessExecutionCapabilityCatalog.IsOgcCallable(definition))
            {
                definition = null;
            }

            if (!string.Equals(processId, CanonicalProcessId, StringComparison.OrdinalIgnoreCase)
                && definition == null)
            {
                OgcProcessesLog.ProcessNotFound(logger, processId);
                return OgcProcessesResults.NoSuchProcess(processId);
            }

            var supportsSync = definition == null
                || (definition.SupportedExecutionModes & ProcessExecutionModes.Sync) != 0;
            var executeSynchronously = supportsSync && !preferAsync;

            OgcExecuteRequest? request;
            try
            {
                var bodyRead = await RequestBodySizeGuard.ReadUtf8TextAsync(
                    context,
                    RequestBodySizeGuard.ResolveMaxBodyBytes(context),
                    cancellationToken)
                    .ConfigureAwait(false);
                if (bodyRead.TooLarge)
                {
                    return bodyRead.ErrorResult!;
                }

                request = JsonSerializer.Deserialize(
                    bodyRead.Body ?? string.Empty,
                    OgcProcessesJsonContext.Default.OgcExecuteRequest);
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

            var rawResponse = string.Equals(request.Response, "raw", StringComparison.OrdinalIgnoreCase);
            if (request.Response != null
                && !string.Equals(request.Response, "document", StringComparison.OrdinalIgnoreCase)
                && !rawResponse)
            {
                OgcProcessesLog.UnsupportedResponseMode(logger, processId, request.Response);
                return OgcProcessesResults.Error(
                    StatusCodes.Status501NotImplemented,
                    "Unsupported response mode",
                    $"Response mode '{request.Response}' is not supported.");
            }

            if (rawResponse && !executeSynchronously)
            {
                return OgcProcessesResults.Error(
                    StatusCodes.Status400BadRequest,
                    "Invalid response mode",
                    "Raw responses require a supported synchronous value request.");
            }

            var geometryService = context.RequestServices.GetRequiredService<IGeometryService>();
            if (!TryBuildAnalysisPlan(
                    processId,
                    request,
                    definition,
                    geometryService,
                    out var analysisPlan,
                    out var parseError))
            {
                OgcProcessesLog.PlanStructureInvalid(logger, processId, parseError ?? "Unknown plan parsing error.");
                return OgcProcessesResults.Error(
                    StatusCodes.Status400BadRequest,
                    "Invalid analysis plan",
                    parseError ?? "The analysis plan payload is invalid.");
            }

            OgcProcessesLog.ExecutionRequested(logger, processId, !executeSynchronously);

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
                    cancellationToken)
                .ConfigureAwait(false);

            OgcProcessesLog.JobCreated(logger, jobRecord.OperationId, processId);

            if (executeSynchronously)
            {
                var terminal = await terminalService.WaitForResultAsync(
                    jobRecord.OperationId,
                    context.User,
                    TimeSpan.FromSeconds(30),
                    cancellationToken).ConfigureAwait(false);
                if (terminal.Outcome is GeoprocessingTerminalResultOutcome.Timeout
                    or GeoprocessingTerminalResultOutcome.ClientDisconnected)
                {
                    terminalService.DispatchOrphanedCancellation(
                        jobRecord.OperationId,
                        context.User,
                        TimeSpan.FromSeconds(10));
                }

                return terminal.Outcome switch
                {
                    GeoprocessingTerminalResultOutcome.Succeeded => rawResponse
                        ? BuildRawResultsResponse(context, terminal.ResultPackage!)
                        : JobEndpoints.BuildResultsResponse(
                            context, logger, jobRecord.OperationId, terminal.ResultPackage!),
                    GeoprocessingTerminalResultOutcome.Failed => OgcProcessesResults.Error(
                        StatusCodes.Status500InternalServerError,
                        "Process execution failed",
                        terminal.Job?.ErrorMessage ?? $"Job '{jobRecord.OperationId}' failed."),
                    GeoprocessingTerminalResultOutcome.Cancelled => OgcProcessesResults.Error(
                        StatusCodes.Status409Conflict,
                        "Process execution cancelled",
                        $"Job '{jobRecord.OperationId}' was cancelled."),
                    GeoprocessingTerminalResultOutcome.NotFound => OgcProcessesResults.NoSuchJob(jobRecord.OperationId),
                    GeoprocessingTerminalResultOutcome.Timeout => OgcProcessesResults.Error(
                        StatusCodes.Status408RequestTimeout,
                        "Process execution timed out",
                        "Synchronous execution did not complete within the bounded wait window. Use respond-async for long-running execution."),
                    GeoprocessingTerminalResultOutcome.ClientDisconnected => context.RequestAborted.IsCancellationRequested
                        ? Results.StatusCode(499)
                        : OgcProcessesResults.Error(
                            StatusCodes.Status408RequestTimeout,
                            "Process execution timed out",
                            "Synchronous execution exceeded the configured request timeout."),
                    _ => throw new InvalidOperationException($"Unexpected terminal result outcome '{terminal.Outcome}'.")
                };
            }

            var baseUrl = BaseUrlResolver.GetBaseUrl(context);
            var statusInfo = OgcProcessesConversionHelpers.ToOgcStatusInfo(jobRecord, processId, baseUrl);

            context.Response.Headers.Location = $"{baseUrl}{BasePath}/jobs/{jobRecord.OperationId}";

            // Acknowledge only a client preference that was actually honored.
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (GeoprocessingAuthorizationException authEx)
        {
            // Log the operation the denied check actually evaluated (e.g.
            // ExecuteMutatingProcess) so a mutating-tier 403 is distinguishable from a
            // baseline Execute denial rather than always reading as Execute (#2798).
            OgcProcessesLog.AuthorizationDenied(
                logger,
                (authEx.ResourceType ?? OperatorResourceType.Process).ToString(),
                (authEx.Operation ?? OperatorOperation.Execute).ToString());
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
        // Intentionally generic: this is the top-level process-execution endpoint
        // boundary; any unanticipated failure must map to a generic 500 rather than
        // crash the request or leak internals to the client.
        catch (Exception ex) when (ex is not OutOfMemoryException)
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

            foreach (var outputValue in (outputsElement.EnumerateArray()).Select(outputElement => outputElement.ValueKind == JsonValueKind.String
                    ? outputElement.GetString()
                    : null))
            {
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
        IGeometryService geometryService,
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
        var inputSrid = TryReadInputSrid(request.Inputs);
        foreach (var input in request.Inputs)
        {
            if (input.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            {
                error = $"Input '{input.Key}' must not be null.";
                return false;
            }

            var parameter = processDefinition.Parameters.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, input.Key, StringComparison.Ordinal));
            if (parameter?.ValueType == ProcessParameterValueType.Wkb
                && input.Value.ValueKind == JsonValueKind.Object)
            {
                if (!TryConvertGeoJsonInput(
                        input.Key,
                        input.Value,
                        inputSrid,
                        geometryService,
                        out var normalized,
                        out error))
                {
                    return false;
                }

                inputs[input.Key] = normalized!;
            }
            else
            {
                inputs[input.Key] = JsonElementToCanonicalInput(input.Value);
            }
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

    private static int? TryReadInputSrid(ImmutableDictionary<string, JsonElement> inputs)
    {
        if (!inputs.TryGetValue("srid", out var srid))
        {
            return null;
        }

        if (srid.ValueKind == JsonValueKind.Number && srid.TryGetInt32(out var numeric))
        {
            return numeric;
        }

        return srid.ValueKind == JsonValueKind.String
            && int.TryParse(srid.GetString(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var text)
            ? text
            : null;
    }

    private static bool TryConvertGeoJsonInput(
        string inputName,
        JsonElement input,
        int? srid,
        IGeometryService geometryService,
        out string? normalized,
        out string? error)
    {
        normalized = null;
        error = null;
        var geoJson = input;
        var isDirectGeoJson = input.ValueKind == JsonValueKind.Object
            && input.TryGetProperty("type", out var type)
            && type.ValueKind == JsonValueKind.String;
        if (!isDirectGeoJson && input.TryGetProperty("value", out var value))
        {
            if (input.TryGetProperty("mediaType", out var mediaType)
                && (mediaType.ValueKind != JsonValueKind.String
                    || !string.Equals(mediaType.GetString(), "application/geo+json", StringComparison.OrdinalIgnoreCase)))
            {
                error = $"Input '{inputName}' declares an unsupported mediaType.";
                return false;
            }

            geoJson = value;
        }

        if (geoJson.ValueKind != JsonValueKind.Object)
        {
            error = $"Input '{inputName}' GeoJSON value must be an object.";
            return false;
        }

        try
        {
            var wkb = geometryService.ConvertGeoJsonToWkb(
                geoJson.GetRawText(),
                srid,
                allowContainers: true);
            if (wkb == null || wkb.Length == 0)
            {
                error = $"Input '{inputName}' must contain a GeoJSON geometry.";
                return false;
            }

            normalized = Convert.ToBase64String(wkb);
            return true;
        }
        catch (ArgumentException)
        {
            error = $"Input '{inputName}' must contain valid GeoJSON geometry.";
            return false;
        }
    }

    private static string JsonElementToCanonicalInput(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => element.GetRawText(),
            _ => element.GetRawText()
        };

    private static bool HasPreference(IEnumerable<string?> values, string preference)
        => values
            .SelectMany(value => (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries))
            .Select(value => value.Split(';', 2, StringSplitOptions.TrimEntries)[0])
            .Any(value => string.Equals(value.Trim(), preference, StringComparison.OrdinalIgnoreCase));

    private static IResult BuildRawResultsResponse(
        HttpContext context,
        AnalysisResultPackage resultPackage)
    {
        if (resultPackage.Artifacts.Count != 1)
        {
            return OgcProcessesResults.Error(
                StatusCodes.Status400BadRequest,
                "Raw response unavailable",
                "Raw response mode requires exactly one value output.");
        }

        var artifact = resultPackage.Artifacts[0];
        var uri = artifact.Uri;
        var separator = uri?.IndexOf(',') ?? -1;
        if (string.IsNullOrWhiteSpace(uri)
            || !uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || separator <= 5)
        {
            return OgcProcessesResults.Error(
                StatusCodes.Status400BadRequest,
                "Raw response unavailable",
                "The single output is a reference and cannot be returned as a raw value.");
        }

        var descriptor = uri[5..separator];
        if (!descriptor.EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
        {
            return OgcProcessesResults.Error(
                StatusCodes.Status400BadRequest,
                "Raw response unavailable",
                "The single output is not an inline base64 value.");
        }

        var mediaTypeSeparator = descriptor.IndexOf(';');
        var mediaType = mediaTypeSeparator > 0
            ? descriptor[..mediaTypeSeparator]
            : artifact.ContentType ?? "application/octet-stream";
        if (!MediaTypeHeaderValue.TryParse(mediaType, out _))
        {
            return OgcProcessesResults.Error(
                StatusCodes.Status500InternalServerError,
                "Invalid process result",
                "The inline result declares an invalid media type.");
        }

        var encoded = uri[(separator + 1)..];
        var maxArtifactBytes = context.RequestServices
            .GetService<IOptions<GeoprocessingExecutorOptions>>()?.Value.MaxArtifactBytes
            ?? 50L * 1024L * 1024L;
        var estimatedBytes = (encoded.Length / 4L) * 3L;
        if (encoded.EndsWith("==", StringComparison.Ordinal))
        {
            estimatedBytes -= 2L;
        }
        else if (encoded.EndsWith('='))
        {
            estimatedBytes -= 1L;
        }
        if (estimatedBytes > maxArtifactBytes)
        {
            return OgcProcessesResults.Error(
                StatusCodes.Status413PayloadTooLarge,
                "Raw response too large",
                "The inline result exceeds the configured artifact response limit.");
        }

        try
        {
            var payload = Convert.FromBase64String(encoded);
            if (payload.LongLength > maxArtifactBytes)
            {
                return OgcProcessesResults.Error(
                    StatusCodes.Status413PayloadTooLarge,
                    "Raw response too large",
                    "The inline result exceeds the configured artifact response limit.");
            }

            return Results.Bytes(payload, mediaType);
        }
        catch (FormatException)
        {
            return OgcProcessesResults.Error(
                StatusCodes.Status500InternalServerError,
                "Invalid process result",
                "The inline result payload is not valid base64 data.");
        }
    }

    private static OgcProcessSummary ToOgcProcessSummary(ProcessDefinition definition, string baseUrl)
        => new()
        {
            Id = definition.ProcessId,
            Title = definition.Title,
            Description = definition.Description,
            Version = "1.0.0",
            JobControlOptions = BuildOgcJobControlOptions(definition),
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
            Description = $"{definition.Description} Execution supports the catalog-advertised job-control modes; asynchronous execution returns document-mode artifact references when the runtime publishes results.",
            Version = "1.0.0",
            JobControlOptions = BuildOgcJobControlOptions(definition),
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

    private static ImmutableArray<string> BuildOgcJobControlOptions(ProcessDefinition definition)
    {
        var options = ImmutableArray.CreateBuilder<string>(2);
        if ((definition.SupportedExecutionModes & ProcessExecutionModes.Async) != 0)
        {
            options.Add("async-execute");
        }

        if ((definition.SupportedExecutionModes & ProcessExecutionModes.Sync) != 0)
        {
            options.Add("sync-execute");
        }

        return options.ToImmutable();
    }

    private static OgcProcessIoDescription ToOgcInputDescription(ProcessParameterSpec parameter)
        => new()
        {
            Title = parameter.DisplayName,
            Description = parameter.Required
                ? $"{parameter.Description} Required."
                : parameter.Description,
            Schema = new OgcProcessIoSchema
            {
                Type = parameter.ValueType == ProcessParameterValueType.Wkb
                    ? null
                    : parameter.ValueType switch
                    {
                        ProcessParameterValueType.WholeNumber or ProcessParameterValueType.Srid => "integer",
                        ProcessParameterValueType.FloatingPoint => "number",
                        ProcessParameterValueType.Flag => "boolean",
                        ProcessParameterValueType.WkbArray => "array",
                        _ => "string"
                    },
                ContentMediaType = parameter.ValueType switch
                {
                    ProcessParameterValueType.WkbArray => "application/json",
                    _ => null
                },
                OneOf = parameter.ValueType == ProcessParameterValueType.Wkb
                    ? ImmutableArray.Create(
                        new OgcProcessIoSchema
                        {
                            Type = "string",
                            ContentMediaType = "application/wkb"
                        },
                        new OgcProcessIoSchema
                        {
                            Type = "object",
                            ContentMediaType = "application/geo+json"
                        })
                    : null
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

    internal static bool TryParseStep(
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

        var rasterSources = new Dictionary<string, RasterSourceDescriptor>(StringComparer.Ordinal);
        if (stepElement.TryGetProperty("rasterSources", out var rasterSourcesElement)
            || stepElement.TryGetProperty("raster_sources", out rasterSourcesElement))
        {
            if (rasterSourcesElement.ValueKind != JsonValueKind.Object)
            {
                error = $"Step '{stepId}' rasterSources must be a JSON object.";
                return false;
            }

            foreach (var property in rasterSourcesElement.EnumerateObject())
            {
                try
                {
                    rasterSources[property.Name] = RasterSourceJson.Deserialize(
                        property.Value.GetRawText());
                }
                catch (JsonException)
                {
                    error = $"Step '{stepId}' raster source '{property.Name}' is invalid.";
                    return false;
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
            RasterSources = rasterSources,
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
        if ((element.TryGetProperty(propertyName, out var property)
                || (alternatePropertyName != null && element.TryGetProperty(alternatePropertyName, out property)))
            && property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString();
            return true;
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
