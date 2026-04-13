// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Ogc.Common;
using Honua.Server.Features.OgcProcesses.Models;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.OgcProcesses;

/// <summary>
/// OGC API Processes process discovery and execution endpoints.
/// </summary>
internal static class ProcessEndpoints
{
    private const string BasePath = "/ogc/processes";
    private const string Tag = "OGC API Processes";

    // V1 stub: single canonical process representing the Honua geoprocessing runtime.
    // CatalogService formalization is follow-on work; this stub allows the OGC adapter
    // to serve a valid process list and description while the catalog is being built.
    internal const string CanonicalProcessId = "honua-geoprocessing";

    // Allowed step kinds mirror AnalysisPlanStepKind (canonical domain enum).
    // The canonical service rejects steps with unrecognized kinds; the OGC adapter
    // must apply the same gate to prevent creating jobs the runtime would reject.
    private static readonly HashSet<string> AllowedStepKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "queryFeatures", "geoprocess", "aggregate", "renderMap", "export"
    };

    // Allowed artifact kinds mirror ArtifactKind (canonical domain enum).
    private static readonly HashSet<string> AllowedArtifactKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "scalar", "featureLayer", "table", "raster", "file", "report", "map", "appBundle"
    };

    private static readonly OgcProcessSummary CanonicalProcessSummary = new()
    {
        Id = CanonicalProcessId,
        Title = "Honua Geoprocessing",
        Description = "Executes an analysis plan through the Honua canonical geoprocessing runtime.",
        Version = "1.0.0",
        JobControlOptions = ImmutableArray.Create("async-execute", "dismiss"),
        OutputTransmission = ImmutableArray.Create("value")
    };

    private static readonly OgcProcessDescription CanonicalProcessDescription = new()
    {
        Id = CanonicalProcessId,
        Title = "Honua Geoprocessing",
        Description = "Executes an analysis plan through the Honua canonical geoprocessing runtime. " +
                      "Accepts a plan specification with steps, inputs, and output expectations. " +
                      "Job status can be polled via the jobs endpoint. " +
                      "Result retrieval will be available once the execution engine is integrated.",
        Version = "1.0.0",
        JobControlOptions = ImmutableArray.Create("async-execute", "dismiss"),
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
        Outputs = ImmutableDictionary.CreateRange(new[]
        {
            KeyValuePair.Create("results", new OgcProcessIoDescription
            {
                Title = "Results",
                Description = "Analysis result package containing artifacts produced by the plan execution.",
                Schema = new OgcProcessIoSchema { Type = "object", ContentMediaType = "application/json" }
            })
        })
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

        endpoints.MapPost($"{BasePath}/processes/{{processId}}/execution", ExecuteProcess)
            .WithTags(Tag)
            .WithName("OgcProcessExecute")
            .WithSummary("Execute a process")
            .Accepts<OgcExecuteRequest>(MediaTypes.Json)
            .Produces<OgcStatusInfo>(StatusCodes.Status201Created)
            .Produces<OgcProcessError>(StatusCodes.Status404NotFound)
            .Produces<OgcProcessError>(StatusCodes.Status501NotImplemented)
            .ExcludeFromDescription();
    }

    private static IResult GetProcessList(
        HttpContext context,
        ILogger<OgcProcessesEndpointsLog> logger)
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

        var processList = new OgcProcessList
        {
            Processes = ImmutableArray.Create(summary),
            Links = ImmutableArray.Create(
                Link.Create($"{baseUrl}{BasePath}/processes", RelationTypes.Self, MediaTypes.Json, "This document"))
        };

        return Results.Json(processList, OgcProcessesJsonContext.Default.OgcProcessList, MediaTypes.Json);
    }

    private static IResult GetProcessDescription(
        string processId,
        HttpContext context,
        ILogger<OgcProcessesEndpointsLog> logger)
    {
        EnrichActivity("GetProcess");
        OgcProcessesLog.ProcessDescriptionRequested(logger, processId);

        if (!string.Equals(processId, CanonicalProcessId, StringComparison.OrdinalIgnoreCase))
        {
            OgcProcessesLog.ProcessNotFound(logger, processId);
            return Results.Json(
                new OgcProcessError
                {
                    Type = "http://www.opengis.net/def/exceptions/ogcapi-processes-1/1.0/no-such-process",
                    Title = "No such process",
                    Status = StatusCodes.Status404NotFound,
                    Detail = $"Process '{processId}' does not exist."
                },
                OgcProcessesJsonContext.Default.OgcProcessError,
                MediaTypes.Json,
                StatusCodes.Status404NotFound);
        }

        var baseUrl = BaseUrlResolver.GetBaseUrl(context);
        var description = CanonicalProcessDescription with
        {
            Links = ImmutableArray.Create(
                Link.Create($"{baseUrl}{BasePath}/processes/{CanonicalProcessId}", RelationTypes.Self, MediaTypes.Json, "This document"),
                Link.Create($"{baseUrl}{BasePath}/processes/{CanonicalProcessId}/execution", "http://www.opengis.net/def/rel/ogc/1.0/execute", MediaTypes.Json, "Execute process"))
        };

        return Results.Json(description, OgcProcessesJsonContext.Default.OgcProcessDescription, MediaTypes.Json);
    }

    private static async Task<IResult> ExecuteProcess(
        string processId,
        [FromBody] OgcExecuteRequest request,
        HttpContext context,
        ILogger<OgcProcessesEndpointsLog> logger,
        IUniversalProgressStore progressStore,
        IOperatorAuthorizationEvaluator authEvaluator,
        IOperatorApprovalEvaluator approvalEvaluator,
        [FromServices] IExecutionJobStore? jobStore = null)
    {
        EnrichActivity("ExecuteProcess");

        var authResult = EvaluateAuthorization(
            authEvaluator, context, logger,
            OperatorResourceType.Process, OperatorOperation.Execute);
        if (authResult != null) return authResult;

        var approvalResult = EvaluateApproval(approvalEvaluator, context, logger);
        if (approvalResult != null) return approvalResult;

        if (!string.Equals(processId, CanonicalProcessId, StringComparison.OrdinalIgnoreCase))
        {
            OgcProcessesLog.ProcessNotFound(logger, processId);
            return Results.Json(
                new OgcProcessError
                {
                    Type = "http://www.opengis.net/def/exceptions/ogcapi-processes-1/1.0/no-such-process",
                    Title = "No such process",
                    Status = StatusCodes.Status404NotFound,
                    Detail = $"Process '{processId}' does not exist."
                },
                OgcProcessesJsonContext.Default.OgcProcessError,
                MediaTypes.Json,
                StatusCodes.Status404NotFound);
        }

        // OGC API Processes: Prefer: respond-async selects async execution.
        // V1 only supports async; sync execution returns 501.
        var preferAsync = context.Request.Headers.TryGetValue("Prefer", out var preferValues)
            && preferValues.Any(v => v != null && v.Contains("respond-async", StringComparison.OrdinalIgnoreCase));

        if (!preferAsync)
        {
            OgcProcessesLog.SyncExecutionNotSupported(logger, processId);
            return Results.Json(
                new OgcProcessError
                {
                    Type = "about:blank",
                    Title = "Synchronous execution not supported",
                    Status = StatusCodes.Status501NotImplemented,
                    Detail = "This process only supports asynchronous execution. " +
                             "Include 'Prefer: respond-async' header in the request."
                },
                OgcProcessesJsonContext.Default.OgcProcessError,
                MediaTypes.Json,
                StatusCodes.Status501NotImplemented);
        }

        // V1: document-only response mode. Reject "raw" or any unsupported value.
        if (request.Response != null
            && !string.Equals(request.Response, "document", StringComparison.OrdinalIgnoreCase))
        {
            OgcProcessesLog.UnsupportedResponseMode(logger, processId, request.Response);
            return Results.Json(
                new OgcProcessError
                {
                    Type = "about:blank",
                    Title = "Unsupported response mode",
                    Status = StatusCodes.Status501NotImplemented,
                    Detail = $"Response mode '{request.Response}' is not supported. " +
                             "V1 only supports 'document' mode."
                },
                OgcProcessesJsonContext.Default.OgcProcessError,
                MediaTypes.Json,
                StatusCodes.Status501NotImplemented);
        }

        // Validate that the request contains the required 'plan' input.
        if (request.Inputs == null
            || !request.Inputs.TryGetValue("plan", out var planElement)
            || planElement.ValueKind == System.Text.Json.JsonValueKind.Undefined
            || planElement.ValueKind == System.Text.Json.JsonValueKind.Null)
        {
            OgcProcessesLog.ExecutionRequestInvalid(logger, processId);
            return Results.Json(
                new OgcProcessError
                {
                    Type = "about:blank",
                    Title = "Invalid execution request",
                    Status = StatusCodes.Status400BadRequest,
                    Detail = "The 'inputs' object must contain a 'plan' property with a non-null analysis plan."
                },
                OgcProcessesJsonContext.Default.OgcProcessError,
                MediaTypes.Json,
                StatusCodes.Status400BadRequest);
        }

        // Validate plan structure: canonical service requires planId and at least one step.
        // This mirrors the validation in HonuaProcessService.SubmitPlanJob to prevent
        // OGC clients from creating jobs that the canonical runtime would reject.
        var planIdError = ValidatePlanStructure(planElement, out var planId);
        if (planIdError != null)
        {
            OgcProcessesLog.PlanStructureInvalid(logger, processId, planIdError);
            return Results.Json(
                new OgcProcessError
                {
                    Type = "about:blank",
                    Title = "Invalid analysis plan",
                    Status = StatusCodes.Status400BadRequest,
                    Detail = planIdError
                },
                OgcProcessesJsonContext.Default.OgcProcessError,
                MediaTypes.Json,
                StatusCodes.Status400BadRequest);
        }

        OgcProcessesLog.ExecutionRequested(logger, processId, true);

        if (jobStore == null)
        {
            OgcProcessesLog.JobStoreUnavailable(logger);
            return Results.Json(
                new OgcProcessError
                {
                    Type = "about:blank",
                    Title = "Service unavailable",
                    Status = StatusCodes.Status503ServiceUnavailable,
                    Detail = "Job operations require Redis-backed durable storage."
                },
                OgcProcessesJsonContext.Default.OgcProcessError,
                MediaTypes.Json,
                StatusCodes.Status503ServiceUnavailable);
        }

        var planJson = planElement.GetRawText();
        var now = DateTimeOffset.UtcNow;
        var jobId = $"gp-{Guid.NewGuid():N}";

        var jobRecord = new ExecutionJobRecord
        {
            OperationId = jobId,
            Status = ExecutionJobStatus.Queued,
            CreatedAt = now,
            UpdatedAt = now,
            CurrentPhase = "Queued",
            Audit = new OperationAuditInfo
            {
                RequestedBy = context.User.Identity?.Name,
                RequestFingerprint = CreateRequestFingerprint(processId, planJson)
            },
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = $"geoprocessing:{planId}"
            }
        };

        await jobStore.TryCreateAsync(jobRecord, cancellationToken: context.RequestAborted).ConfigureAwait(false);

        var progress = GeoprocessingProgress.CreateForSubmittedJob(jobId, planId);
        await progressStore.SetProgressAsync(jobId, progress, TimeSpan.FromDays(7), context.RequestAborted)
            .ConfigureAwait(false);

        OgcProcessesLog.JobCreated(logger, jobId, processId);

        var baseUrl = BaseUrlResolver.GetBaseUrl(context);
        var statusInfo = OgcProcessesConversionHelpers.ToOgcStatusInfo(jobRecord, processId, baseUrl);

        context.Response.Headers["Location"] = $"{baseUrl}{BasePath}/jobs/{jobId}";
        context.Response.Headers["Preference-Applied"] = "respond-async";

        return Results.Json(statusInfo, OgcProcessesJsonContext.Default.OgcStatusInfo, MediaTypes.Json, StatusCodes.Status201Created);
    }

    /// <summary>
    /// Validates plan structure matches the canonical service invariants:
    /// non-empty planId, at least one step, recognized step kinds, and recognized output artifact kinds.
    /// </summary>
    /// <returns>Error message if invalid; null if valid.</returns>
    private static string? ValidatePlanStructure(
        System.Text.Json.JsonElement plan,
        out string planId)
    {
        planId = string.Empty;

        if (plan.ValueKind != System.Text.Json.JsonValueKind.Object)
            return "The 'plan' input must be a JSON object.";

        // Require planId (mirrors HonuaProcessService.EnsurePlanExecutable)
        if (!plan.TryGetProperty("planId", out var planIdProp)
            && !plan.TryGetProperty("plan_id", out planIdProp))
        {
            return "The analysis plan must contain a 'planId' property.";
        }

        if (planIdProp.ValueKind != System.Text.Json.JsonValueKind.String)
            return "The analysis plan 'planId' must be a string.";

        planId = planIdProp.GetString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(planId))
            return "The analysis plan 'planId' must not be empty.";

        // Require at least one step (mirrors HonuaProcessService.EnsurePlanExecutable)
        if (!plan.TryGetProperty("steps", out var stepsProp)
            || stepsProp.ValueKind != System.Text.Json.JsonValueKind.Array
            || stepsProp.GetArrayLength() == 0)
        {
            return "The analysis plan must contain at least one step.";
        }

        // Validate step kinds, input value types, and dependsOn shape
        // (mirrors HonuaProcessService.ValidatePlanStructure which rejects steps with
        // Unspecified or undefined PlanStepKind values, and the canonical proto contract
        // which defines inputs as map<string,string> and depends_on as repeated string).
        foreach (var step in stepsProp.EnumerateArray())
        {
            if (step.ValueKind != System.Text.Json.JsonValueKind.Object)
                return "Each step in the analysis plan must be a JSON object.";

            var stepId = (step.TryGetProperty("stepId", out var sid) || step.TryGetProperty("step_id", out sid))
                ? (sid.ValueKind == System.Text.Json.JsonValueKind.String ? sid.GetString() ?? "<unknown>" : "<unknown>")
                : "<unknown>";

            if (!step.TryGetProperty("kind", out var kindProp)
                || kindProp.ValueKind != System.Text.Json.JsonValueKind.String)
            {
                return $"Step '{stepId}' is missing a 'kind' property.";
            }

            var kindStr = kindProp.GetString();
            if (string.IsNullOrWhiteSpace(kindStr) || !AllowedStepKinds.Contains(kindStr))
            {
                return $"Step '{stepId}' has unsupported step kind '{kindStr}'.";
            }

            // Validate step inputs are map<string,string> per canonical proto contract.
            if (step.TryGetProperty("inputs", out var inputsProp))
            {
                if (inputsProp.ValueKind != System.Text.Json.JsonValueKind.Object)
                    return $"Step '{stepId}' inputs must be a JSON object.";

                foreach (var input in inputsProp.EnumerateObject())
                {
                    if (input.Value.ValueKind != System.Text.Json.JsonValueKind.String)
                        return $"Step '{stepId}' input '{input.Name}' must be a string value.";
                }
            }

            // Validate dependsOn is an array of strings per canonical proto contract.
            if ((step.TryGetProperty("dependsOn", out var depsProp) || step.TryGetProperty("depends_on", out depsProp))
                && depsProp.ValueKind != System.Text.Json.JsonValueKind.Null)
            {
                if (depsProp.ValueKind != System.Text.Json.JsonValueKind.Array)
                    return $"Step '{stepId}' dependsOn must be an array.";

                foreach (var dep in depsProp.EnumerateArray())
                {
                    if (dep.ValueKind != System.Text.Json.JsonValueKind.String)
                        return $"Step '{stepId}' dependsOn values must be strings.";
                }
            }
        }

        // Validate output artifact kinds if present (mirrors HonuaProcessService.ValidatePlanStructure
        // which rejects Unspecified or undefined ArtifactKind values).
        // The canonical proto contract defines outputs as repeated ArtifactKind, so require an array.
        if (plan.TryGetProperty("outputs", out var outputsProp)
            && outputsProp.ValueKind != System.Text.Json.JsonValueKind.Null)
        {
            if (outputsProp.ValueKind != System.Text.Json.JsonValueKind.Array)
                return "The 'outputs' property must be an array of artifact kind strings.";

            foreach (var output in outputsProp.EnumerateArray())
            {
                if (output.ValueKind != System.Text.Json.JsonValueKind.String)
                    return "Output artifact kinds must be strings.";

                var outputStr = output.GetString();
                if (string.IsNullOrWhiteSpace(outputStr) || !AllowedArtifactKinds.Contains(outputStr))
                    return $"Unsupported artifact kind '{outputStr}'.";
            }
        }

        return null;
    }

    private static string CreateRequestFingerprint(string processId, string planJson)
    {
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes($"ogc-execute:{processId}:{planJson}"));
        return Convert.ToHexString(hashBytes.AsSpan(0, 12)).ToLowerInvariant();
    }

    /// <summary>
    /// Evaluates operator authorization and returns an error result if denied; null if allowed.
    /// Mirrors the authorization gate in <c>HonuaProcessService.EnsureAuthorized</c>.
    /// </summary>
    internal static IResult? EvaluateAuthorization(
        IOperatorAuthorizationEvaluator authEvaluator,
        HttpContext context,
        ILogger logger,
        OperatorResourceType resourceType,
        OperatorOperation operation)
    {
        var decision = authEvaluator.Evaluate(context.User, new OperatorAuthorizationRequest
        {
            ResourceType = resourceType,
            Operation = operation
        });

        if (decision.IsAllowed) return null;

        OgcProcessesLog.AuthorizationDenied(logger, resourceType.ToString(), operation.ToString());

        if (decision.RequiresAuthentication)
        {
            return Results.Json(
                new OgcProcessError
                {
                    Type = "about:blank",
                    Title = "Authentication required",
                    Status = StatusCodes.Status401Unauthorized,
                    Detail = "Authentication is required for this operation."
                },
                OgcProcessesJsonContext.Default.OgcProcessError,
                MediaTypes.Json,
                StatusCodes.Status401Unauthorized);
        }

        return Results.Json(
            new OgcProcessError
            {
                Type = "about:blank",
                Title = "Permission denied",
                Status = StatusCodes.Status403Forbidden,
                Detail = "You do not have permission to perform this operation."
            },
            OgcProcessesJsonContext.Default.OgcProcessError,
            MediaTypes.Json,
            StatusCodes.Status403Forbidden);
    }

    /// <summary>
    /// Evaluates operator approval and returns an error result if approval is required; null if not.
    /// Mirrors the approval gate in <c>HonuaProcessService.EnsureApproved</c>.
    /// </summary>
    private static IResult? EvaluateApproval(
        IOperatorApprovalEvaluator approvalEvaluator,
        HttpContext context,
        ILogger logger)
    {
        var approval = approvalEvaluator.Evaluate(
            context.User,
            new OperatorAuthorizationRequest
            {
                ResourceType = OperatorResourceType.Process,
                Operation = OperatorOperation.Execute
            });

        if (!approval.IsRequired) return null;

        OgcProcessesLog.ExecutionRejectedApprovalRequired(logger, approval.PolicyRef ?? "unknown");

        return Results.Json(
            new OgcProcessError
            {
                Type = "about:blank",
                Title = "Approval required",
                Status = StatusCodes.Status403Forbidden,
                Detail = $"This operation requires approval (policy: {approval.PolicyRef}). " +
                         "Contact an administrator to request approval for this operation."
            },
            OgcProcessesJsonContext.Default.OgcProcessError,
            MediaTypes.Json,
            StatusCodes.Status403Forbidden);
    }

    private static void EnrichActivity(string operation)
    {
        var activity = Activity.Current;
        if (activity == null) return;
        activity.SetTag(HonuaTelemetry.Tags.Protocol, "OGC-API-Processes");
        activity.SetTag(HonuaTelemetry.Tags.Operation, operation);
    }

}
