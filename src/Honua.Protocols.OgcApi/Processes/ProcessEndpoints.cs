// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Geoprocessing.Raster;
using Honua.Core.Features.Infrastructure.Validation;
using Honua.Geoprocessing;
using Honua.Infrastructure.Helpers;
using Honua.Protocols.Ogc.Common;
using Honua.Protocols.Ogc.Api.Processes.Models;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

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
            .Produces<OgcProcessError>(StatusCodes.Status400BadRequest)
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
            .Produces(StatusCodes.Status200OK, contentType: MediaTypes.GeoJson)
            .Produces<OgcStatusInfo>(StatusCodes.Status201Created)
            .Produces<OgcProcessError>(StatusCodes.Status400BadRequest)
            .Produces<OgcProcessError>(StatusCodes.Status401Unauthorized)
            .Produces<OgcProcessError>(StatusCodes.Status403Forbidden)
            .Produces<OgcProcessError>(StatusCodes.Status404NotFound)
            .Produces<OgcProcessError>(StatusCodes.Status409Conflict)
            .Produces<OgcProcessError>(StatusCodes.Status410Gone)
            .Produces<OgcProcessError>(StatusCodes.Status413PayloadTooLarge)
            .Produces<OgcProcessError>(StatusCodes.Status408RequestTimeout)
            .Produces(StatusCodes.Status499ClientClosedRequest)
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
        IOgcProcessesCatalog processCatalog)
    {
        EnrichActivity("GetProcessList");
        OgcProcessesLog.ProcessListRequested(logger);

        if (!TryParseProcessListLimit(context.Request, out var limit))
        {
            return OgcProcessesResults.Error(
                StatusCodes.Status400BadRequest,
                "Invalid limit",
                "The 'limit' parameter must be a positive integer.");
        }

        if (!TryParseProcessListOffset(context.Request, out var offset))
        {
            return OgcProcessesResults.Error(
                StatusCodes.Status400BadRequest,
                "Invalid offset",
                "The 'offset' parameter must be a non-negative integer.");
        }

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

        var allProcesses = ImmutableArray.CreateBuilder<OgcProcessSummary>();
        allProcesses.Add(summary);
        foreach (var definition in processCatalog.ListProcesses()
                     .Where(IsPublishedOgcProcess)
                     .OrderBy(process => process.ProcessId, StringComparer.Ordinal))
        {
            allProcesses.Add(ToOgcProcessSummary(definition, baseUrl));
        }

        var pageStart = Math.Min(offset, allProcesses.Count);
        var available = allProcesses.Count - pageStart;
        var pageSize = limit.HasValue ? Math.Min(limit.Value, available) : available;
        var page = allProcesses
            .Skip(pageStart)
            .Take(pageSize)
            .ToImmutableArray();

        var processListUrl = $"{baseUrl}{BasePath}/processes";
        var selfUrl = BuildProcessListUrl(processListUrl, limit, offset);
        var links = ImmutableArray.CreateBuilder<Link>();
        links.Add(
            Link.Create(
                selfUrl,
                RelationTypes.Self,
                MediaTypes.Json,
                "This document"));

        var nextOffset = pageStart + pageSize;
        if (limit.HasValue && nextOffset < allProcesses.Count)
        {
            links.Add(
                Link.Create(
                    BuildProcessListUrl(processListUrl, limit, nextOffset),
                    RelationTypes.Next,
                    MediaTypes.Json,
                    "Next page"));
        }

        var processList = new OgcProcessList
        {
            Processes = page,
            Links = links.ToImmutable()
        };

        return Results.Json(processList, OgcProcessesJsonContext.Default.OgcProcessList, MediaTypes.Json);
    }

    private static bool TryParseProcessListLimit(HttpRequest request, out int? limit)
    {
        limit = null;
        if (!request.Query.TryGetValue("limit", out var values))
        {
            return true;
        }

        if (values.Count != 1 ||
            !int.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ||
            parsed <= 0)
        {
            return false;
        }

        limit = parsed;
        return true;
    }

    private static bool TryParseProcessListOffset(HttpRequest request, out int offset)
    {
        offset = 0;
        if (!request.Query.TryGetValue("offset", out var values))
        {
            return true;
        }

        return values.Count == 1
            && int.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out offset);
    }

    private static string BuildProcessListUrl(string processListUrl, int? limit, int offset)
    {
        if (!limit.HasValue)
        {
            return offset == 0
                ? processListUrl
                : $"{processListUrl}?offset={offset.ToString(CultureInfo.InvariantCulture)}";
        }

        var url = $"{processListUrl}?limit={limit.Value.ToString(CultureInfo.InvariantCulture)}";
        return offset == 0
            ? url
            : $"{url}&offset={offset.ToString(CultureInfo.InvariantCulture)}";
    }

    private static IResult GetProcessDescription(
        string processId,
        HttpContext context,
        ILogger<OgcProcessesEndpointsLog> logger,
        IOgcProcessesCatalog processCatalog)
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
        if (definition == null || !IsPublishedOgcProcess(definition))
        {
            OgcProcessesLog.ProcessNotFound(logger, processId);
            return OgcProcessesResults.NoSuchProcess(processId);
        }

        var baseUrl = BaseUrlResolver.GetBaseUrl(context);
        if (OgcProcessesCiteEchoFixture.IsDefinition(definition))
        {
            return Results.Json(
                OgcProcessesCiteEchoFixture.CreateDescription(baseUrl),
                OgcProcessesJsonContext.Default.OgcProcessDescription,
                MediaTypes.Json);
        }

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
        IOgcProcessesCatalog processCatalog,
        IHttpClientFactory httpClientFactory)
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
            if (definition != null && !IsPublishedOgcProcess(definition))
            {
                definition = null;
            }

            if (!string.Equals(processId, CanonicalProcessId, StringComparison.OrdinalIgnoreCase)
                && definition == null)
            {
                OgcProcessesLog.ProcessNotFound(logger, processId);
                return OgcProcessesResults.NoSuchProcess(processId);
            }

            // The canonical process accepts arbitrary multi-step plans whose inner
            // processes may be async-only. Keep that plan surface asynchronous;
            // synchronous execution is safe only for a concrete catalog definition
            // that explicitly advertises the capability.
            var supportsSync = definition != null
                && (definition.SupportedExecutionModes & ProcessExecutionModes.Sync) != 0;
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

            if (definition != null
                && OgcProcessesCiteEchoFixture.IsDefinition(definition)
                && !OgcProcessesCiteEchoFixture.TryValidateInputs(request.Inputs, out var inputError))
            {
                OgcProcessesLog.PlanStructureInvalid(
                    logger,
                    processId,
                    inputError ?? "Unknown CITE echo input validation error.");
                return OgcProcessesResults.Error(
                    StatusCodes.Status400BadRequest,
                    "Invalid process input",
                    inputError ?? "The CITE echo process input is invalid.");
            }

            if (definition == null && request.Outputs is { Count: > 0 })
            {
                return OgcProcessesResults.Error(
                    StatusCodes.Status400BadRequest,
                    "Invalid output selection",
                    $"Process '{processId}' does not support explicit output selection.");
            }

            if (definition == null && rawResponse)
            {
                return OgcProcessesResults.Error(
                    StatusCodes.Status400BadRequest,
                    "Invalid response mode",
                    "The canonical plan process has no declared value outputs and requires document mode. Use a catalog process for raw results.");
            }

            if (definition != null && !OgcProcessesCiteEchoFixture.IsDefinition(definition))
            {
                var normalized = await NormalizeInputReferencesAsync(
                    request,
                    definition,
                    httpClientFactory,
                    context.RequestServices.GetService<IOptions<GeoprocessingExecutorOptions>>()?.Value.MaxArtifactBytes
                        ?? 50L * 1024L * 1024L,
                    cancellationToken).ConfigureAwait(false);
                if (normalized.Request == null)
                {
                    return OgcProcessesResults.Error(
                        StatusCodes.Status400BadRequest,
                        "Invalid process input",
                        normalized.Error ?? "A referenced input could not be resolved.");
                }

                request = normalized.Request;
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
                metadata[OgcProcessesExecutionMetadata.ResponseMode] = rawResponse ? "raw" : "document";
            }
            if (definition != null)
            {
                if (OgcProcessesCiteEchoFixture.IsDefinition(definition))
                {
                    if (!OgcProcessesCiteEchoFixture.TryAddOutputBindings(
                            metadata,
                            request.Inputs,
                            request.Outputs,
                            out var outputError))
                    {
                        return OgcProcessesResults.Error(
                            StatusCodes.Status400BadRequest,
                            "Invalid output selection",
                            outputError ?? "The requested output selection is invalid.");
                    }
                }
                else
                {
                    if (!TryAddOutputBindings(
                            metadata,
                            definition,
                            request.Outputs,
                            out var outputError))
                    {
                        return OgcProcessesResults.Error(
                            StatusCodes.Status400BadRequest,
                            "Invalid output selection",
                            outputError ?? "The requested output selection is invalid.");
                    }
                }
            }

            var submissionCatalog = definition != null
                && OgcProcessesCiteEchoFixture.IsDefinition(definition)
                    ? processCatalog
                    : processCatalog.CanonicalCatalog;
            var jobRecord = await jobService
                .SubmitProtocolJobAsync(
                    analysisPlan!,
                    idempotencyKey: null,
                    context.User,
                    submissionCatalog,
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
                        ? await JobEndpoints.BuildRawResultsResponseAsync(
                                terminal.Job ?? jobRecord, context, terminal.ResultPackage!)
                            .ConfigureAwait(false)
                        : await JobEndpoints.BuildValueResultsResponseAsync(
                                terminal.Job ?? jobRecord, context, terminal.ResultPackage!)
                            .ConfigureAwait(false),
                    GeoprocessingTerminalResultOutcome.Failed => JobEndpoints.BuildJobFailedResult(
                        jobRecord.OperationId,
                        terminal.Job?.ErrorMessage),
                    GeoprocessingTerminalResultOutcome.Cancelled => JobEndpoints.BuildJobDismissedResult(
                        jobRecord.OperationId),
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
        catch (GeoprocessingStoreUnavailableException storeEx)
        {
            OgcProcessesLog.JobStoreUnavailable(logger);
            return OgcProcessesResults.StoreUnavailable(storeEx);
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

            if (!TryParseAnalysisPlan(planElement, out plan, out error))
            {
                return false;
            }

            // The certification echo fixture is a protocol-only process. A
            // canonical plan is executed as `honua-geoprocessing`, so allowing
            // an echo step here would create a job whose durable protocol
            // identity cannot be handled by the echo executor.
            if (plan!.Steps.Any(step =>
                    string.Equals(
                        step.ProcessId,
                        OgcProcessesCiteEchoFixture.ProcessId,
                        StringComparison.OrdinalIgnoreCase)))
            {
                error = $"Canonical plans cannot contain protocol-only process '{OgcProcessesCiteEchoFixture.ProcessId}'.";
                plan = null;
                return false;
            }

            return true;
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

            if (OgcProcessesCiteEchoFixture.IsDefinition(processDefinition))
            {
                inputs[input.Key] = input.Value.GetRawText();
                continue;
            }

            var parameter = processDefinition.Parameters.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, input.Key, StringComparison.Ordinal));
            var effectiveValue = GetInlineInputValue(input.Value, out var mediaType);
            if (effectiveValue.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                error = $"Input '{input.Key}' must not be null.";
                return false;
            }

            if (parameter?.ValueType == ProcessParameterValueType.Wkb
                && !string.IsNullOrWhiteSpace(mediaType)
                && !string.Equals(GetMediaTypeEssence(mediaType), "application/wkb", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(GetMediaTypeEssence(mediaType), "application/geo+json", StringComparison.OrdinalIgnoreCase))
            {
                error = $"Input '{input.Key}' declares an unsupported mediaType.";
                return false;
            }

            if (parameter?.ValueType == ProcessParameterValueType.Wkb
                && string.Equals(GetMediaTypeEssence(mediaType), "application/wkb", StringComparison.OrdinalIgnoreCase))
            {
                if (effectiveValue.ValueKind != JsonValueKind.String)
                {
                    error = $"Input '{input.Key}' application/wkb value must be a base64 string.";
                    return false;
                }

                inputs[input.Key] = effectiveValue.GetString() ?? string.Empty;
            }
            else if (parameter?.ValueType == ProcessParameterValueType.Wkb
                && effectiveValue.ValueKind == JsonValueKind.Object)
            {
                if (!TryConvertGeoJsonInput(
                        input.Key,
                        effectiveValue,
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
                inputs[input.Key] = parameter?.AcceptsGeoJsonDataUri == true
                    && effectiveValue.ValueKind == JsonValueKind.Object
                        ? "data:application/geo+json;base64," + Convert.ToBase64String(
                            System.Text.Encoding.UTF8.GetBytes(effectiveValue.GetRawText()))
                        : JsonElementToCanonicalInput(effectiveValue);
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

        srid = GetInlineInputValue(srid, out _);
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

    internal static bool TryConvertGeoJsonInput(
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
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
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

    private static async Task<InputNormalizationResult> NormalizeInputReferencesAsync(
        OgcExecuteRequest request,
        ProcessDefinition definition,
        IHttpClientFactory httpClientFactory,
        long maxArtifactBytes,
        CancellationToken cancellationToken)
    {
        if (request.Inputs == null)
        {
            return new InputNormalizationResult(request, null);
        }

        var parameterNames = definition.Parameters.Select(parameter => parameter.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var inputName in request.Inputs.Keys)
        {
            if (!parameterNames.Contains(inputName))
            {
                return new InputNormalizationResult(null, $"Unknown input '{inputName}' for process '{definition.ProcessId}'.");
            }
        }

        // The catalog bounds the number of references; the shared byte budget bounds
        // their aggregate payload before any resolved values are retained together.
        var remainingBytes = maxArtifactBytes;
        var inputs = request.Inputs.ToBuilder();
        foreach (var input in request.Inputs)
        {
            if (input.Value.ValueKind != JsonValueKind.Object
                || !input.Value.TryGetProperty("href", out var hrefElement))
            {
                continue;
            }

            if (hrefElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(hrefElement.GetString()))
            {
                return new InputNormalizationResult(null, $"Input '{input.Key}' href must be a non-empty string.");
            }

            var mediaTypeHint = input.Value.TryGetProperty("type", out var typeElement)
                && typeElement.ValueKind == JsonValueKind.String
                    ? typeElement.GetString()
                    : null;
            var resolved = await ResolveInputReferenceAsync(
                hrefElement.GetString()!,
                mediaTypeHint,
                httpClientFactory,
                remainingBytes,
                cancellationToken).ConfigureAwait(false);
            if (resolved.Value.ValueKind == JsonValueKind.Undefined)
            {
                return new InputNormalizationResult(
                    null,
                    $"Input '{input.Key}' reference could not be resolved: {resolved.Error}");
            }

            inputs[input.Key] = BuildQualifiedInput(resolved.Value, resolved.MediaType);
            remainingBytes -= resolved.SizeBytes;
        }

        return new InputNormalizationResult(
            request with { Inputs = inputs.ToImmutable() },
            null);
    }

    private static async Task<ResolvedInputReference> ResolveInputReferenceAsync(
        string href,
        string? mediaTypeHint,
        IHttpClientFactory httpClientFactory,
        long maxArtifactBytes,
        CancellationToken cancellationToken)
    {
        byte[] payload;
        string? mediaType = mediaTypeHint;
        if (href.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryDecodeDataUri(href, maxArtifactBytes, out payload, out var dataMediaType, out var dataError))
            {
                return new ResolvedInputReference(default, null, dataError);
            }

            mediaType ??= dataMediaType;
        }
        else
        {
            var validation = await OutboundHttpUrlValidator.ValidateAsync(href, cancellationToken)
                .ConfigureAwait(false);
            if (!validation.IsValid)
            {
                return new ResolvedInputReference(default, null, validation.ErrorMessage);
            }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, validation.Uri);
                using var response = await httpClientFactory
                    .CreateClient(OgcProcessInputReferenceHttpClient.Name)
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return new ResolvedInputReference(
                        default,
                        null,
                        $"the remote server returned HTTP {(int)response.StatusCode}.");
                }

                if (response.Content.Headers.ContentLength is > 0
                    && response.Content.Headers.ContentLength > maxArtifactBytes)
                {
                    return new ResolvedInputReference(default, null, "the referenced value exceeds the configured input limit.");
                }

                mediaType ??= response.Content.Headers.ContentType?.MediaType;
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                var read = await ReadBoundedAsync(stream, maxArtifactBytes, cancellationToken).ConfigureAwait(false);
                if (read == null)
                {
                    return new ResolvedInputReference(default, null, "the referenced value exceeds the configured input limit.");
                }

                payload = read;
            }
            catch (HttpRequestException)
            {
                return new ResolvedInputReference(default, null, "the remote value is unavailable.");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new ResolvedInputReference(default, null, "the remote value request timed out.");
            }
        }

        try
        {
            if (IsJsonMediaType(mediaType))
            {
                using var document = JsonDocument.Parse(payload);
                return new ResolvedInputReference(document.RootElement.Clone(), mediaType, null, payload.LongLength);
            }

            var scalarValue = GetMediaTypeEssence(mediaType)?.StartsWith("text/", StringComparison.OrdinalIgnoreCase) == true
                ? System.Text.Encoding.UTF8.GetString(payload)
                : Convert.ToBase64String(payload);
            using var scalar = JsonDocument.Parse(JsonSerializer.Serialize(
                scalarValue,
                OgcProcessesJsonContext.Default.String));
            return new ResolvedInputReference(
                scalar.RootElement.Clone(),
                mediaType ?? "application/octet-stream",
                null,
                payload.LongLength);
        }
        catch (JsonException)
        {
            return new ResolvedInputReference(default, null, "the referenced JSON value is invalid.");
        }
    }

    private static JsonElement BuildQualifiedInput(JsonElement value, string? mediaType)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("value");
            value.WriteTo(writer);
            if (!string.IsNullOrWhiteSpace(mediaType))
            {
                writer.WriteString("mediaType", mediaType);
            }

            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    internal static bool TryDecodeDataUri(
        string uri,
        long maxArtifactBytes,
        out byte[] payload,
        out string? mediaType,
        out string? error)
    {
        payload = [];
        mediaType = null;
        error = null;
        var separator = uri.IndexOf(',');
        if (!uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || separator <= 5)
        {
            error = "the value is not a valid data URI.";
            return false;
        }

        var descriptor = uri[5..separator];
        var base64 = descriptor.EndsWith(";base64", StringComparison.OrdinalIgnoreCase);
        var mediaTypeSeparator = descriptor.IndexOf(';');
        mediaType = mediaTypeSeparator >= 0 ? descriptor[..mediaTypeSeparator] : descriptor;
        if (string.IsNullOrWhiteSpace(mediaType))
        {
            mediaType = "text/plain";
        }

        var encodedPayload = Uri.UnescapeDataString(uri[(separator + 1)..]);
        if (base64 && encodedPayload.Length > ((maxArtifactBytes + 2L) / 3L) * 4L)
        {
            error = "the referenced value exceeds the configured input limit.";
            return false;
        }

        try
        {
            payload = base64
                ? Convert.FromBase64String(encodedPayload)
                : System.Text.Encoding.UTF8.GetBytes(encodedPayload);
        }
        catch (Exception exception) when (exception is FormatException or UriFormatException)
        {
            error = "the data URI payload is malformed.";
            return false;
        }

        if (payload.LongLength > maxArtifactBytes)
        {
            payload = [];
            error = "the referenced value exceeds the configured input limit.";
            return false;
        }

        return true;
    }

    private static async Task<byte[]?> ReadBoundedAsync(
        Stream stream,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return buffer.ToArray();
            }

            if (buffer.Length + read > maxBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }
    }

    private static bool IsJsonMediaType(string? mediaType)
    {
        var essence = GetMediaTypeEssence(mediaType);
        return !string.IsNullOrWhiteSpace(essence)
               && (essence.EndsWith("/json", StringComparison.OrdinalIgnoreCase)
                   || essence.EndsWith("+json", StringComparison.OrdinalIgnoreCase));
    }

    private static string? GetMediaTypeEssence(string? mediaType)
        => mediaType?.Split(';', 2, StringSplitOptions.TrimEntries)[0];

    private readonly record struct InputNormalizationResult(OgcExecuteRequest? Request, string? Error);

    private readonly record struct ResolvedInputReference(JsonElement Value, string? MediaType, string? Error, long SizeBytes = 0);

    private static JsonElement GetInlineInputValue(JsonElement input, out string? mediaType)
    {
        mediaType = null;
        if (input.ValueKind != JsonValueKind.Object
            || (input.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String)
            || !input.TryGetProperty("value", out var value))
        {
            return input;
        }

        if (input.TryGetProperty("mediaType", out var mediaTypeElement)
            && mediaTypeElement.ValueKind == JsonValueKind.String)
        {
            mediaType = mediaTypeElement.GetString();
        }
        else if (input.TryGetProperty("format", out var format)
                 && format.ValueKind == JsonValueKind.Object
                 && format.TryGetProperty("mediaType", out mediaTypeElement)
                 && mediaTypeElement.ValueKind == JsonValueKind.String)
        {
            mediaType = mediaTypeElement.GetString();
        }

        return value;
    }

    private static bool HasPreference(IEnumerable<string?> values, string preference)
        => values
            .SelectMany(value => (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries))
            .Select(value => value.Split(';', 2, StringSplitOptions.TrimEntries)[0])
            .Any(value => string.Equals(value.Trim(), preference, StringComparison.OrdinalIgnoreCase));

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

    private static bool IsPublishedOgcProcess(ProcessDefinition definition)
        => ProcessExecutionCapabilityCatalog.IsOgcCallable(definition)
           || OgcProcessesCiteEchoFixture.IsDefinition(definition);

    private static OgcProcessDescription ToOgcProcessDescription(ProcessDefinition definition, string baseUrl)
        => new()
        {
            Id = definition.ProcessId,
            Title = definition.Title,
            Description = $"{definition.Description} Execution supports the catalog-advertised job-control modes and returns outputs using the advertised value transmission.",
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
            MinOccurs = parameter.Required ? 1 : 0,
            Schema = new OgcProcessIoSchema
            {
                Type = parameter.ValueType == ProcessParameterValueType.Wkb || parameter.AcceptsGeoJsonDataUri
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
                OneOf = parameter.ValueType == ProcessParameterValueType.Wkb || parameter.AcceptsGeoJsonDataUri
                    ? ImmutableArray.Create(
                        new OgcProcessIoSchema
                        {
                            Type = "string",
                            Format = parameter.AcceptsGeoJsonDataUri ? "uri" : "byte",
                            ContentMediaType = parameter.AcceptsGeoJsonDataUri ? null : "application/wkb"
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
                Schema = GetDefaultOutputSchema(kind)
            };
        }

        return outputs.ToImmutable();
    }

    internal static string GetDefaultOutputContentMediaType(ArtifactKind kind)
        => kind switch
        {
            ArtifactKind.FeatureLayer => "application/geo+json",
            ArtifactKind.Raster => "image/tiff",
            ArtifactKind.Table or ArtifactKind.Report or ArtifactKind.Scalar or ArtifactKind.Map => MediaTypes.Json,
            ArtifactKind.File or ArtifactKind.AppBundle => "application/octet-stream",
            _ => "application/octet-stream"
        };

    internal static OgcProcessIoSchema GetDefaultOutputSchema(ArtifactKind kind)
    {
        var isBinary = kind is ArtifactKind.Raster or ArtifactKind.File or ArtifactKind.AppBundle;
        return new OgcProcessIoSchema
        {
            Type = isBinary ? "string" : "object",
            Format = isBinary ? "binary" : null,
            ContentMediaType = GetDefaultOutputContentMediaType(kind)
        };
    }

    internal static bool TryAddOutputBindings(
        Dictionary<string, string> metadata,
        ProcessDefinition definition,
        ImmutableDictionary<string, JsonElement>? requestedOutputs,
        out string? error)
    {
        error = null;
        var available = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < definition.OutputArtifactKinds.Count; index++)
        {
            var outputName = BuildOutputName(
                definition.OutputArtifactKinds[index],
                index,
                definition.OutputArtifactKinds);
            available[outputName] = index;
        }

        if (requestedOutputs is { Count: > 0 })
        {
            var unknown = requestedOutputs.Keys
                .Where(outputName => !available.ContainsKey(outputName))
                .OrderBy(outputName => outputName, StringComparer.Ordinal)
                .ToArray();
            if (unknown.Length > 0)
            {
                error = $"Unknown output(s) for process '{definition.ProcessId}': {string.Join(", ", unknown)}.";
                return false;
            }

            foreach (var requestedOutput in requestedOutputs)
            {
                if (requestedOutput.Value.ValueKind != JsonValueKind.Object)
                {
                    error = $"Output '{requestedOutput.Key}' must be an object.";
                    return false;
                }

                var unsupportedProperties = requestedOutput.Value
                    .EnumerateObject()
                    .Where(property => !string.Equals(
                        property.Name,
                        "transmissionMode",
                        StringComparison.Ordinal))
                    .Select(property => property.Name)
                    .OrderBy(property => property, StringComparer.Ordinal)
                    .ToArray();
                if (unsupportedProperties.Length > 0)
                {
                    error = $"Output '{requestedOutput.Key}' contains unsupported field(s): "
                        + $"{string.Join(", ", unsupportedProperties)}.";
                    return false;
                }

                if (requestedOutput.Value.TryGetProperty("transmissionMode", out var transmissionMode)
                    && (transmissionMode.ValueKind != JsonValueKind.String
                        || !string.Equals(
                            transmissionMode.GetString(),
                            "value",
                            StringComparison.OrdinalIgnoreCase)))
                {
                    error = $"Output '{requestedOutput.Key}' only supports value transmission.";
                    return false;
                }
            }
        }

        var selected = available.Keys
            .Where(outputName => requestedOutputs is not { Count: > 0 }
                || requestedOutputs.ContainsKey(outputName))
            .OrderBy(outputName => available[outputName])
            .ToArray();
        foreach (var outputName in selected)
        {
            // Bind the advertised name to its original artifact slot. The durable
            // plan retains the process definition's complete output-kind ordering,
            // so compacting a non-leading selection into slot zero would relabel a
            // different artifact and make result filtering return the wrong output.
            metadata[$"{GeoprocessingProtocolMetadataKeys.OutputNamePrefix}{available[outputName]}"] = outputName;
        }

        return true;
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
