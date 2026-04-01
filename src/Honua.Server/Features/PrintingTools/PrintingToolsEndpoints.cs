// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Server.Features.Infrastructure.Services;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Progress;
using Honua.Server.Features.PrintingTools.Layout;
using Honua.Server.Features.PrintingTools.Models;

namespace Honua.Server.Features.PrintingTools;

/// <summary>
/// Maps GeoServices-compatible print service endpoints for the Export Web Map Task.
/// </summary>
internal static class PrintingToolsEndpoints
{
    private const string TaskRoute = "/rest/services/Utilities/PrintingTools/GPServer/Export Web Map Task";
    private const string TemplatesTaskRoute = "/rest/services/Utilities/PrintingTools/GPServer/Get Layout Templates Info Task";

    /// <summary>
    /// Maps print service endpoints.
    /// </summary>
    public static IEndpointRouteBuilder MapPrintingToolsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Service info / metadata
        endpoints.MapGet(TaskRoute,
                static (HttpContext context, CancellationToken cancellationToken) => HandleServiceInfo(context, cancellationToken))
            .WithDisplayName("Print Service Info")
            .WithName("PrintingToolsServiceInfo")
            .WithSummary("Get print service task metadata")
            .WithDescription("Returns metadata about the Export Web Map Task including available templates and formats")
            .WithTags("PrintingTools");

        // Synchronous execute
        endpoints.MapPost($"{TaskRoute}/execute",
                static (HttpContext context, CancellationToken cancellationToken) => HandleExecute(context, cancellationToken))
            .WithDisplayName("Execute Print Task")
            .WithName("PrintingToolsExecute")
            .WithSummary("Execute a synchronous print task")
            .WithDescription("Composes a map layout from a web map JSON definition and returns the output")
            .WithTags("PrintingTools");

        endpoints.MapGet($"{TaskRoute}/execute",
                static (HttpContext context, CancellationToken cancellationToken) => HandleExecute(context, cancellationToken))
            .WithDisplayName("Execute Print Task (GET)")
            .WithName("PrintingToolsExecuteGet")
            .WithSummary("Execute a synchronous print task using GET")
            .WithDescription("Composes a map layout from a web map JSON definition and returns the output")
            .WithTags("PrintingTools");

        // Async submit job (POST + GET per GP contract)
        endpoints.MapPost($"{TaskRoute}/submitJob",
                static (HttpContext context, CancellationToken cancellationToken) => HandleSubmitJob(context, cancellationToken))
            .WithDisplayName("Submit Print Job")
            .WithName("PrintingToolsSubmitJob")
            .WithSummary("Submit an asynchronous print job")
            .WithDescription("Queues a print job for background processing and returns a job ID")
            .WithTags("PrintingTools");

        endpoints.MapGet($"{TaskRoute}/submitJob",
                static (HttpContext context, CancellationToken cancellationToken) => HandleSubmitJob(context, cancellationToken))
            .WithDisplayName("Submit Print Job (GET)")
            .WithName("PrintingToolsSubmitJobGet")
            .WithSummary("Submit an asynchronous print job using GET")
            .WithDescription("Queues a print job for background processing and returns a job ID")
            .WithTags("PrintingTools");

        // Job status
        endpoints.MapGet($"{TaskRoute}/jobs/{{jobId}}",
                static (HttpContext context, CancellationToken cancellationToken) => HandleJobStatus(context, cancellationToken))
            .WithDisplayName("Get Print Job Status")
            .WithName("PrintingToolsJobStatus")
            .WithSummary("Get the status of an async print job")
            .WithDescription("Returns the current status and progress of a print job")
            .WithTags("PrintingTools");

        // Job result download
        endpoints.MapGet($"{TaskRoute}/jobs/{{jobId}}/results/Output_File",
                static (HttpContext context, CancellationToken cancellationToken) => HandleJobResult(context, cancellationToken))
            .WithDisplayName("Get Print Job Result")
            .WithName("PrintingToolsJobResult")
            .WithSummary("Get the output file from a completed print job")
            .WithDescription("Returns the download URL for the output file of a completed print job")
            .WithTags("PrintingTools");

        // Get Layout Templates Info Task (separate GP task per Esri contract)
        endpoints.MapGet($"{TemplatesTaskRoute}/execute",
                static (HttpContext context, CancellationToken cancellationToken) => HandleGetLayoutTemplatesInfo(context, cancellationToken))
            .WithDisplayName("Get Layout Templates Info")
            .WithName("PrintingToolsGetLayoutTemplatesInfo")
            .WithSummary("Returns layout template metadata")
            .WithDescription("Returns metadata about available layout templates in the standard GP result shape")
            .WithTags("PrintingTools");

        return endpoints;
    }

    private static async Task<IResult> HandleServiceInfo(HttpContext context, CancellationToken cancellationToken)
    {
        var templateNames = LayoutTemplateRegistry.GetTemplateNames();

        // Resolve edition to filter advertised choices.
        // Uses ILicenseStatusProvider (config-driven, matches StaticMap and admin read endpoints).
        // ILicenseManager is a separate in-memory placeholder for the mutable admin API.
        // #338 will unify both behind a single persistent license source.
        var licenseProvider = context.RequestServices.GetRequiredService<ILicenseStatusProvider>();
        var edition = licenseProvider.GetCurrentStatus().Edition;
        var isPro = edition >= HonuaEdition.Pro;

        var formatChoices = isPro
            ? new[] { PrintOutputFormat.Pdf, PrintOutputFormat.Png32, PrintOutputFormat.Jpg }
            : new[] { PrintOutputFormat.Png32, PrintOutputFormat.Jpg };
        // defaultFormat must match ResolveFormat's fallback (PNG32) so clients
        // that omit the Format parameter get the same result the metadata promises.
        // PDF is still available in the choiceList for Pro editions.
        var defaultFormat = PrintOutputFormat.Png32;
        var templateChoices = isPro ? [.. templateNames] : new[] { "MAP_ONLY" };

        var response = new PrintServiceInfoResponse
        {
            Name = "Export Web Map Task",
            DisplayName = "Export Web Map",
            Parameters =
            [
                new PrintServiceParameter
                {
                    Name = "Web_Map_as_JSON",
                    DataType = "GPString",
                    DisplayName = "Web Map as JSON",
                    Direction = "esriGPParameterDirectionInput",
                    DefaultValue = ""
                },
                new PrintServiceParameter
                {
                    Name = "Format",
                    DataType = "GPString",
                    DisplayName = "Format",
                    Direction = "esriGPParameterDirectionInput",
                    DefaultValue = defaultFormat,
                    ChoiceList = formatChoices
                },
                new PrintServiceParameter
                {
                    Name = "Layout_Template",
                    DataType = "GPString",
                    DisplayName = "Layout Template",
                    Direction = "esriGPParameterDirectionInput",
                    DefaultValue = "MAP_ONLY",
                    ChoiceList = templateChoices
                },
                new PrintServiceParameter
                {
                    Name = "Output_File",
                    DataType = "GPDataFile",
                    DisplayName = "Output File",
                    Direction = "esriGPParameterDirectionOutput"
                }
            ]
        };

        return Results.Json(response, PrintingToolsJsonContext.Default.PrintServiceInfoResponse,
            contentType: "application/json");
    }

    private static async Task<IResult> HandleGetLayoutTemplatesInfo(HttpContext context, CancellationToken cancellationToken)
    {
        var licenseProvider = context.RequestServices.GetRequiredService<ILicenseStatusProvider>();
        var edition = licenseProvider.GetCurrentStatus().Edition;
        var isPro = edition >= HonuaEdition.Pro;

        var allTemplates = LayoutTemplateRegistry.GetTemplates();
        var templates = isPro ? allTemplates : allTemplates.Where(t => t.IsMapOnly).ToList();

        var templateInfos = templates.Select(t => new EsriLayoutTemplateInfo
        {
            LayoutTemplate = t.Name,
            PageSize = [Math.Round(t.PageWidth / 72.0, 3), Math.Round(t.PageHeight / 72.0, 3)],
            PageUnits = "inches",
            WebMapFrameSize = [Math.Round(t.MapFrame.Width / 72.0, 3), Math.Round(t.MapFrame.Height / 72.0, 3)],
            LayoutOptions = new EsriLayoutOptions
            {
                TitleText = new EsriLayoutElement { Type = "esriLayoutTextElement", IsVisible = t.Title is not null },
                LegendOptions = new EsriLayoutElement { Type = "esriLayoutLegendElement", IsVisible = t.Legend is not null },
                ScaleBarOptions = new EsriLayoutElement { Type = "esriLayoutScaleBarElement", IsVisible = t.ScaleBar is not null },
                CopyrightText = new EsriLayoutElement { Type = "esriLayoutTextElement", IsVisible = t.Attribution is not null }
            }
        }).ToArray();

        var response = new LayoutTemplatesInfoResponse
        {
            Results =
            [
                new LayoutTemplatesInfoResult
                {
                    ParamName = "Output_JSON",
                    DataType = "GPString",
                    Value = templateInfos
                }
            ],
            Messages = []
        };

        return Results.Json(response, PrintingToolsJsonContext.Default.LayoutTemplatesInfoResponse,
            contentType: "application/json");
    }

    /// <summary>
    /// Validated print request parameters shared by sync execute and async submit paths.
    /// </summary>
    private readonly record struct ValidatedPrintRequest(
        WebMapDefinition WebMap,
        string Format,
        string TemplateName,
        int Dpi,
        ILogger Logger);

    /// <summary>
    /// Validates and resolves print request parameters from the HTTP context.
    /// Returns either a validated request or an <see cref="IResult"/> error.
    /// </summary>
    private static async Task<(ValidatedPrintRequest? Request, IResult? Error)> ValidateAndResolveRequestAsync(
        HttpContext context, CancellationToken cancellationToken)
    {
        var loggerFactory = context.RequestServices.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger("Honua.Server.PrintingToolsEndpoints");

        var (webMapJson, format, templateName) = await ReadRequestParametersAsync(context);

        var webMap = PrintingToolsRequestHandlers.ParseWebMapJson(webMapJson);
        if (webMap is null)
        {
            return (null, StandardErrorHelpers.CreateBadRequest(context,
                "Invalid or missing Web_Map_as_JSON parameter."));
        }

        var extentError = PrintingToolsRequestHandlers.ValidateWebMapExtent(webMap);
        if (extentError is not null)
        {
            return (null, StandardErrorHelpers.CreateBadRequest(context, extentError));
        }

        var resolvedFormat = PrintingToolsRequestHandlers.ResolveFormat(format);
        if (!PrintOutputFormat.IsSupported(resolvedFormat))
        {
            return (null, StandardErrorHelpers.CreateBadRequest(context,
                $"Output format '{resolvedFormat}' is not supported. Supported formats: PDF, PNG32, JPG."));
        }

        var resolvedTemplate = templateName ?? "MAP_ONLY";
        if (!LayoutTemplateRegistry.TryGetTemplate(resolvedTemplate, out var template))
        {
            return (null, StandardErrorHelpers.CreateBadRequest(context,
                $"Layout template '{resolvedTemplate}' is not available."));
        }

        var outputSizeError = PrintingToolsRequestHandlers.ValidateMapOnlyOutputSize(webMap, resolvedTemplate);
        if (outputSizeError is not null)
        {
            return (null, StandardErrorHelpers.CreateBadRequest(context, outputSizeError));
        }

        var dpi = PrintingToolsRequestHandlers.ResolveDpi(webMap);

        var licenseProvider = context.RequestServices.GetRequiredService<ILicenseStatusProvider>();
        var edition = licenseProvider.GetCurrentStatus().Edition;
        var editionError = PrintingToolsRequestHandlers.ValidateEdition(
            template, resolvedFormat, edition, logger);
        if (editionError is not null)
        {
            return (null, StandardErrorHelpers.CreateForbidden(context, editionError));
        }

        return (new ValidatedPrintRequest(webMap, resolvedFormat, resolvedTemplate, dpi, logger), null);
    }

    private static async Task<IResult> HandleExecute(HttpContext context, CancellationToken cancellationToken)
    {
        var (validated, error) = await ValidateAndResolveRequestAsync(context, cancellationToken);
        if (error is not null) return error;
        var req = validated!.Value;

        // Acquire a concurrency lease to bound Skia surface memory (parity with
        // RasterRenderCapacityLimiter on the MapServer export/tile paths).
        var concurrencyGate = context.RequestServices.GetRequiredService<PrintRenderConcurrencyGate>();
        await using var renderLease = await concurrencyGate.TryAcquireAsync(cancellationToken);
        if (renderLease is null)
        {
            return StandardErrorHelpers.CreateServiceUnavailable(
                context,
                PrintRenderConcurrencyGate.CapacityExceededMessage,
                PrintRenderConcurrencyGate.RetryAfterSeconds);
        }

        PrintingToolsLog.ExecuteRequested(req.Logger, req.TemplateName, req.Format, req.Dpi);
        var sw = Stopwatch.StartNew();

        var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
        var featureReader = context.RequestServices.GetRequiredService<IFeatureReader>();
        var styleCatalog = context.RequestServices.GetRequiredService<ILayerStyleCatalog>();
        var accessPolicyEvaluator = context.RequestServices.GetRequiredService<IAccessPolicyEvaluator>();

        (byte[] OutputBytes, string ContentType, string FileName)? result;
        try
        {
            result = await PrintingToolsRequestHandlers.ExecuteAsync(
                req.WebMap, req.Format, req.TemplateName, req.Dpi,
                resourceValidator, featureReader, styleCatalog, req.Logger, cancellationToken,
                callerPrincipal: context.User, accessPolicyEvaluator: accessPolicyEvaluator);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PrintingToolsLog.ExecuteFailed(req.Logger, req.TemplateName, req.Format, ex.Message, ex);
            return StandardErrorHelpers.CreateInternalServerError(context,
                "An error occurred while processing the print request.");
        }

        if (result is null)
        {
            PrintingToolsLog.ExecuteFailed(req.Logger, req.TemplateName, req.Format,
                "Print composition returned null.");
            return StandardErrorHelpers.CreateBadRequest(context,
                "Print composition failed. Check template, format, and edition compatibility.");
        }

        var (outputBytes, contentType, fileName) = result.Value;
        sw.Stop();
        PrintingToolsLog.ExecuteCompleted(req.Logger, req.TemplateName, req.Format, outputBytes.LongLength, sw.Elapsed.TotalMilliseconds);

        // Check response format preference
        var responseFormat = context.Request.Query["f"].FirstOrDefault()
            ?? (context.Request.HasFormContentType ? (await context.Request.ReadFormAsync(cancellationToken))["f"].FirstOrDefault() : null);

        if (string.Equals(responseFormat, "json", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(responseFormat, "pjson", StringComparison.OrdinalIgnoreCase))
        {
            // Store as a temporary file and return an HTTP-served download URL
            var temporaryFileService = context.RequestServices.GetRequiredService<ITemporaryFileService>();
            string downloadUrl;
            try
            {
                downloadUrl = await temporaryFileService.StoreTemporaryFileAsync(
                    outputBytes,
                    contentType,
                    PrintingToolsRequestHandlers.ResultTtl,
                    principal: context.User,
                    cancellationToken: cancellationToken);
            }
            catch (TemporaryStorageLimitExceededException ex)
            {
                return StandardErrorHelpers.CreateServiceUnavailable(
                    context,
                    "Temporary printing storage is currently at capacity. Please retry shortly.",
                    ex.RetryAfterSeconds);
            }

            var warnings = PrintingToolsRequestHandlers.CollectWarnings(req.WebMap, req.Logger);
            var jsonResponse = new PrintExecuteResponse
            {
                Results =
                [
                    new PrintResult
                    {
                        ParamName = "Output_File",
                        DataType = "GPDataFile",
                        Value = new PrintResultValue { Url = downloadUrl }
                    }
                ],
                Messages = warnings
            };

            return Results.Json(jsonResponse, PrintingToolsJsonContext.Default.PrintExecuteResponse,
                contentType: "application/json");
        }

        // Return binary output directly
        return Results.Bytes(outputBytes, contentType, fileName);
    }

    private static async Task<IResult> HandleSubmitJob(HttpContext context, CancellationToken cancellationToken)
    {
        var (validated, error) = await ValidateAndResolveRequestAsync(context, cancellationToken);
        if (error is not null) return error;
        var req = validated!.Value;

        return await SubmitJobInternalAsync(context, req.WebMap, req.Format, req.TemplateName, req.Dpi, req.Logger, cancellationToken);
    }

    private static async Task<IResult> SubmitJobInternalAsync(
        HttpContext context,
        WebMapDefinition webMap,
        string format,
        string templateName,
        int dpi,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var channel = context.RequestServices.GetRequiredService<Channel<PrintJob>>();

        var jobId = Guid.NewGuid().ToString("N");
        var totalElements = 1 + (webMap.OperationalLayers?.Length ?? 0); // map frame + layers
        var job = new PrintJob(jobId, webMap, format, templateName, dpi, totalElements, CallerPrincipal: context.User);

        // Persist progress BEFORE publishing to the channel so a fast worker
        // cannot finish and have its Completed state overwritten by a late Queued write.
        // Collect warnings eagerly so they're visible in job status from the start
        // (parity with the sync execute path).
        var submitWarnings = PrintingToolsRequestHandlers.CollectWarnings(webMap, logger);
        var warningDescriptions = submitWarnings.Length > 0
            ? submitWarnings.Select(w => w.Description ?? "").Where(d => d.Length > 0).ToList()
            : (IReadOnlyList<string>)[];

        var progressStore = context.RequestServices.GetRequiredService<IUniversalProgressStore>();
        var progress = PrintProgress.CreateInitial(jobId, format, templateName, totalElements) with
        {
            Warnings = warningDescriptions
        };
        await progressStore.SetProgressAsync(jobId, progress, PrintingToolsRequestHandlers.ResultTtl, cancellationToken);

        // Attempt non-blocking enqueue; return 503 if queue is full
        if (!channel.Writer.TryWrite(job))
        {
            await progressStore.DeleteProgressAsync(jobId, cancellationToken);
            return StandardErrorHelpers.CreateServiceUnavailable(context,
                "Print queue is full. Please retry later.", retryAfterSeconds: 30);
        }

        PrintingToolsLog.JobSubmitted(logger, jobId, templateName, format);

        var response = new PrintSubmitJobResponse
        {
            JobId = jobId,
            JobStatus = "esriJobSubmitted"
        };

        return Results.Json(response, PrintingToolsJsonContext.Default.PrintSubmitJobResponse,
            contentType: "application/json", statusCode: 202);
    }

    private static async Task<IResult> HandleJobStatus(HttpContext context, CancellationToken cancellationToken)
    {
        var loggerFactory = context.RequestServices.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger("Honua.Server.PrintingToolsEndpoints");

        var jobId = context.Request.RouteValues["jobId"]?.ToString();
        if (string.IsNullOrWhiteSpace(jobId))
        {
            return StandardErrorHelpers.CreateBadRequest(context, "Missing jobId.");
        }

        PrintingToolsLog.JobStatusQueried(logger, jobId);

        var progressStore = context.RequestServices.GetRequiredService<IUniversalProgressStore>();
        var progress = await progressStore.GetProgressAsync(jobId, cancellationToken);

        if (progress is not PrintProgress printProgress)
        {
            return StandardErrorHelpers.CreateNotFound(context, $"Print job '{jobId}' not found.");
        }

        var jobStatus = MapOperationStatusToEsriJobStatus(printProgress.Status);
        var messages = new List<PrintJobMessage>
        {
            new() { Type = "esriJobMessageTypeInformative", Description = printProgress.CurrentPhase }
        };

        // Surface persisted warnings (parity with sync execute path)
        foreach (var warning in printProgress.Warnings)
        {
            messages.Add(new PrintJobMessage
            {
                Type = "esriJobMessageTypeWarning",
                Description = warning
            });
        }

        if (printProgress.ErrorMessage is not null)
        {
            messages.Add(new PrintJobMessage
            {
                Type = "esriJobMessageTypeError",
                Description = printProgress.ErrorMessage
            });
        }

        // Per GP contract, include results references when job succeeded
        Dictionary<string, PrintJobResultRef>? results = null;
        if (jobStatus == "esriJobSucceeded")
        {
            results = new Dictionary<string, PrintJobResultRef>
            {
                ["Output_File"] = new PrintJobResultRef { ParamUrl = "results/Output_File" }
            };
        }

        var response = new PrintJobStatusResponse
        {
            JobId = jobId,
            JobStatus = jobStatus,
            Messages = [.. messages],
            Results = results
        };

        return Results.Json(response, PrintingToolsJsonContext.Default.PrintJobStatusResponse,
            contentType: "application/json");
    }

    private static async Task<IResult> HandleJobResult(HttpContext context, CancellationToken cancellationToken)
    {
        var jobId = context.Request.RouteValues["jobId"]?.ToString();
        if (string.IsNullOrWhiteSpace(jobId))
        {
            return StandardErrorHelpers.CreateBadRequest(context, "Missing jobId.");
        }

        var progressStore = context.RequestServices.GetRequiredService<IUniversalProgressStore>();
        var progress = await progressStore.GetProgressAsync(jobId, cancellationToken);

        if (progress is not PrintProgress printProgress)
        {
            return StandardErrorHelpers.CreateNotFound(context, $"Print job '{jobId}' not found.");
        }

        if (printProgress.Status != OperationStatus.Completed || string.IsNullOrWhiteSpace(printProgress.DownloadUrl))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                $"Print job '{jobId}' is not yet complete (status: {printProgress.Status}).");
        }

        // Return single GP parameter object per the result-parameter resource contract
        var response = new PrintResult
        {
            ParamName = "Output_File",
            DataType = "GPDataFile",
            Value = new PrintResultValue { Url = printProgress.DownloadUrl }
        };

        return Results.Json(response, PrintingToolsJsonContext.Default.PrintResult,
            contentType: "application/json");
    }

    private static async Task<(string? WebMapJson, string? Format, string? TemplateName)> ReadRequestParametersAsync(
        HttpContext context)
    {
        string? webMapJson;
        string? format;
        string? templateName;

        if (context.Request.HasFormContentType)
        {
            var form = await context.Request.ReadFormAsync();
            webMapJson = form["Web_Map_as_JSON"].FirstOrDefault();
            format = form["Format"].FirstOrDefault();
            templateName = form["Layout_Template"].FirstOrDefault();
        }
        else
        {
            webMapJson = context.Request.Query["Web_Map_as_JSON"].FirstOrDefault();
            format = context.Request.Query["Format"].FirstOrDefault();
            templateName = context.Request.Query["Layout_Template"].FirstOrDefault();
        }

        return (webMapJson, format, templateName);
    }

    private static string MapOperationStatusToEsriJobStatus(OperationStatus status) => status switch
    {
        OperationStatus.Queued => "esriJobSubmitted",
        OperationStatus.Processing => "esriJobExecuting",
        OperationStatus.Completed => "esriJobSucceeded",
        OperationStatus.Failed => "esriJobFailed",
        OperationStatus.Cancelled => "esriJobCancelled",
        _ => "esriJobSubmitted"
    };
}
