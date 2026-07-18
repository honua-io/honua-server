// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing;
using Honua.Infrastructure.Helpers;
using Honua.ServiceDefaults;

namespace Honua.Protocols.Ogc.Classic.Wps20;

/// <summary>
/// Thin WPS 2.0.2 XML adapter over the canonical process catalog and job service.
/// </summary>
internal static partial class Wps20Endpoint
{
    internal const string WpsNamespace = "http://www.opengis.net/wps/2.0";
    internal const string OwsNamespace = "http://www.opengis.net/ows/2.0";
    private const string Version = "2.0.0";
    private const int MaxInputs = 64;
    private const int MaxInputCharacters = 65_536;
    private static readonly string[] Methods = ["GET", "POST"];

    internal static IEndpointRouteBuilder MapWps20Endpoint(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapMethods("/wps", Methods, DispatchAsync)
            .WithDisplayName("WPS 2.0.2 Service")
            .WithName("Wps20Service")
            .WithSummary("OGC Web Processing Service 2.0.2")
            .WithDescription("Provides GetCapabilities, DescribeProcess, Execute, GetStatus, and GetResult over the canonical geoprocessing runtime")
            .WithTags("WPS 2.0", "OGC")
            .CacheOutput(policy => policy.NoCache())
            .Produces(200, contentType: "application/xml")
            .Produces(400, contentType: "application/xml")
            .Produces(401, contentType: "application/xml")
            .Produces(403, contentType: "application/xml")
            .Produces(404, contentType: "application/xml")
            .AllowAnonymous();

        return endpoints;
    }

    private static async Task<IResult> DispatchAsync(
        HttpContext context,
        IProcessCatalog catalog,
        IGeoprocessingJobService jobs,
        ILogger<Wps20EndpointLog> logger)
    {
        WpsRequest request;
        try
        {
            request = await ReadRequestAsync(context).ConfigureAwait(false);
        }
        catch (WpsRequestException ex)
        {
            Log.InvalidRequest(logger, ex.Message);
            return Exception(ex.Code, ex.Message, ex.Locator, ex.StatusCode);
        }
        catch (XmlException ex)
        {
            Log.InvalidRequest(logger, ex.Message);
            return Exception("InvalidParameterValue", "The XML request is not valid or contains prohibited constructs.", "request");
        }

        using var activity = HonuaTelemetry.ActivitySource.StartActivity("ogc.wps." + request.Operation.ToLowerInvariant());
        activity?.SetTag(HonuaTelemetry.Tags.Protocol, "WPS-2.0.2");
        activity?.SetTag(HonuaTelemetry.Tags.Operation, request.Operation);
        if (request.JobId is not null)
        {
            activity?.SetTag(HonuaTelemetry.Tags.JobId, request.JobId);
        }
        context.Items["__honua_request_operation"] = "wps." + request.Operation.ToLowerInvariant();
        Log.OperationRequested(logger, request.Operation);

        try
        {
            return request.Operation.ToUpperInvariant() switch
            {
                "GETCAPABILITIES" => GetCapabilities(context, catalog),
                "DESCRIBEPROCESS" => DescribeProcess(catalog, request.Identifier),
                "EXECUTE" => await ExecuteAsync(context, catalog, jobs, request).ConfigureAwait(false),
                "GETSTATUS" => await GetStatusAsync(context, jobs, request.JobId).ConfigureAwait(false),
                "GETRESULT" => await GetResultAsync(jobs, context.User, request.JobId, context.RequestAborted).ConfigureAwait(false),
                _ => Exception("OperationNotSupported", $"Operation '{request.Operation}' is not implemented.", "request", StatusCodes.Status501NotImplemented)
            };
        }
        catch (GeoprocessingAuthorizationException ex)
        {
            return Exception("AccessDenied", ex.RequiresAuthentication ? "Authentication is required." : "The caller is not authorized.", null,
                ex.RequiresAuthentication ? StatusCodes.Status401Unauthorized : StatusCodes.Status403Forbidden);
        }
        catch (GeoprocessingNotFoundException)
        {
            return Exception("NoSuchJob", "The requested job does not exist.", "jobId", StatusCodes.Status404NotFound);
        }
        catch (GeoprocessingValidationException ex)
        {
            return Exception("InvalidParameterValue", ex.Message, "input");
        }
        catch (GeoprocessingStoreUnavailableException)
        {
            return Exception("ServerBusy", "The job store is unavailable.", null, StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static IResult GetCapabilities(HttpContext context, IProcessCatalog catalog)
    {
        var endpoint = OgcClassicRequestHelpers.EscapeXml($"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}/wps");
        var offerings = string.Join(string.Empty, catalog.ListProcesses().OrderBy(p => p.ProcessId, StringComparer.Ordinal).Select(p =>
            $"<wps:ProcessSummary processVersion=\"1.0.0\" jobControlOptions=\"async-execute\" outputTransmission=\"value\"><ows:Title>{X(p.Title)}</ows:Title><ows:Identifier>{X(p.ProcessId)}</ows:Identifier></wps:ProcessSummary>"));
        return Xml($"""
            <?xml version="1.0" encoding="UTF-8"?>
            <wps:Capabilities xmlns:wps="{WpsNamespace}" xmlns:ows="{OwsNamespace}" version="{Version}">
              <ows:ServiceIdentification><ows:Title>Honua Web Processing Service</ows:Title><ows:ServiceType>WPS</ows:ServiceType><ows:ServiceTypeVersion>{Version}</ows:ServiceTypeVersion></ows:ServiceIdentification>
              <ows:OperationsMetadata>
                {Operation("GetCapabilities", endpoint)}{Operation("DescribeProcess", endpoint)}{Operation("Execute", endpoint)}{Operation("GetStatus", endpoint)}{Operation("GetResult", endpoint)}
              </ows:OperationsMetadata>
              <wps:Contents>{offerings}</wps:Contents>
            </wps:Capabilities>
            """);
    }

    private static IResult DescribeProcess(IProcessCatalog catalog, string? identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return Exception("MissingParameterValue", "An Identifier is required.", "identifier");
        }

        var process = catalog.GetProcess(identifier);
        if (process is null)
        {
            return Exception("NoSuchProcess", $"Process '{identifier}' does not exist.", "identifier", StatusCodes.Status404NotFound);
        }

        var inputs = string.Join(string.Empty, process.Parameters.Select(parameter =>
            $"<wps:Input minOccurs=\"{(parameter.Required ? "1" : "0")}\" maxOccurs=\"1\"><ows:Title>{X(parameter.DisplayName)}</ows:Title><ows:Abstract>{X(parameter.Description)}</ows:Abstract><ows:Identifier>{X(parameter.Name)}</ows:Identifier><wps:LiteralData><wps:Format mimeType=\"text/plain\" default=\"true\"/></wps:LiteralData></wps:Input>"));
        const string outputs = "<wps:Output><ows:Title>Result summary</ows:Title><ows:Identifier>result</ows:Identifier><wps:LiteralData><wps:Format mimeType=\"text/plain\" default=\"true\"/></wps:LiteralData></wps:Output>";

        return Xml($"""
            <?xml version="1.0" encoding="UTF-8"?>
            <wps:ProcessOfferings xmlns:wps="{WpsNamespace}" xmlns:ows="{OwsNamespace}">
              <wps:ProcessOffering jobControlOptions="async-execute" outputTransmission="value">
                <wps:Process processVersion="1.0.0"><ows:Title>{X(process.Title)}</ows:Title><ows:Abstract>{X(process.Description)}</ows:Abstract><ows:Identifier>{X(process.ProcessId)}</ows:Identifier>{inputs}{outputs}</wps:Process>
              </wps:ProcessOffering>
            </wps:ProcessOfferings>
            """);
    }

    private static async Task<IResult> ExecuteAsync(HttpContext context, IProcessCatalog catalog, IGeoprocessingJobService jobs, WpsRequest request)
    {
        await jobs.EnsureCallerAuthorizedAsync(context.User, OperatorResourceType.Process, OperatorOperation.Execute, context.RequestAborted).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(request.Identifier))
        {
            return Exception("MissingParameterValue", "An Identifier is required.", "identifier");
        }

        var process = catalog.GetProcess(request.Identifier);
        if (process is null)
        {
            return Exception("NoSuchProcess", $"Process '{request.Identifier}' does not exist.", "identifier", StatusCodes.Status404NotFound);
        }

        var known = process.Parameters.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknown = request.Inputs.Keys.FirstOrDefault(key => !known.Contains(key));
        if (unknown is not null)
        {
            return Exception("InvalidParameterValue", $"Input '{unknown}' is not defined by process '{process.ProcessId}'.", unknown);
        }
        var missing = process.Parameters.FirstOrDefault(p => p.Required && !request.Inputs.ContainsKey(p.Name) && p.DefaultValue is null);
        if (missing is not null)
        {
            return Exception("MissingParameterValue", $"Required input '{missing.Name}' is missing.", missing.Name);
        }

        var inputValues = process.Parameters
            .Where(p => request.Inputs.ContainsKey(p.Name) || p.DefaultValue is not null)
            .ToDictionary(p => p.Name, p => request.Inputs.GetValueOrDefault(p.Name) ?? p.DefaultValue!, StringComparer.Ordinal);
        var plan = new AnalysisPlan
        {
            PlanId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
            IntentId = "wps-2.0",
            Outputs = process.OutputArtifactKinds,
            Steps = [new AnalysisPlanStep { StepId = "wps-step", Kind = AnalysisPlanStepKind.Geoprocess, ProcessId = process.ProcessId, Inputs = inputValues }]
        };
        var job = await jobs.SubmitJobAsync(plan, null, context.User,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["submittedVia"] = "WPS-2.0.2", ["protocolProcessId"] = process.ProcessId },
            context.RequestAborted).ConfigureAwait(false);
        var location = $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}/wps?service=WPS&request=GetStatus&version={Version}&jobId={Uri.EscapeDataString(job.OperationId)}";
        context.Response.Headers.Location = location;
        return StatusInfo(job, StatusCodes.Status201Created);
    }

    private static async Task<IResult> GetStatusAsync(HttpContext context, IGeoprocessingJobService jobs, string? jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            return Exception("MissingParameterValue", "A JobID is required.", "jobId");
        }
        var job = await jobs.GetJobAsync(jobId, context.User, context.RequestAborted).ConfigureAwait(false);
        return StatusInfo(job);
    }

    private static async Task<IResult> GetResultAsync(IGeoprocessingJobService jobs, System.Security.Claims.ClaimsPrincipal user, string? jobId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            return Exception("MissingParameterValue", "A JobID is required.", "jobId");
        }
        var package = await jobs.GetJobResultsAsync(jobId, user, cancellationToken).ConfigureAwait(false);
        return Xml($"<?xml version=\"1.0\" encoding=\"UTF-8\"?><wps:Result xmlns:wps=\"{WpsNamespace}\" xmlns:ows=\"{OwsNamespace}\" jobID=\"{X(jobId)}\"><wps:Output><ows:Identifier>result</ows:Identifier><wps:Data mimeType=\"text/plain\"><wps:LiteralValue>{X(package.Summary.Title)}</wps:LiteralValue></wps:Data></wps:Output></wps:Result>");
    }

    private static IResult StatusInfo(ExecutionJobRecord job, int statusCode = StatusCodes.Status200OK)
    {
        var status = job.Status switch
        {
            ExecutionJobStatus.Queued or ExecutionJobStatus.Provisioning => "Accepted",
            ExecutionJobStatus.Running => "Running",
            ExecutionJobStatus.Succeeded => "Succeeded",
            ExecutionJobStatus.Failed => "Failed",
            ExecutionJobStatus.Cancelled => "Dismissed",
            _ => "Failed"
        };
        var percent = job.PercentComplete is double value
            ? $"<wps:PercentCompleted>{((int)Math.Round(Math.Clamp(value, 0, 100), MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture)}</wps:PercentCompleted>"
            : string.Empty;
        return Xml($"<?xml version=\"1.0\" encoding=\"UTF-8\"?><wps:StatusInfo xmlns:wps=\"{WpsNamespace}\"><wps:JobID>{X(job.OperationId)}</wps:JobID><wps:Status>{status}</wps:Status>{percent}</wps:StatusInfo>", statusCode);
    }

    private static async Task<WpsRequest> ReadRequestAsync(HttpContext context)
    {
        if (HttpMethods.IsGet(context.Request.Method))
        {
            return FromValues(context.Request.Query.ToDictionary(pair => pair.Key, pair => pair.Value.ToString(), StringComparer.OrdinalIgnoreCase));
        }
        if (context.Request.HasFormContentType)
        {
            var form = await context.Request.ReadFormAsync(context.RequestAborted).ConfigureAwait(false);
            return FromValues(form.ToDictionary(pair => pair.Key, pair => pair.Value.ToString(), StringComparer.OrdinalIgnoreCase));
        }

        var settings = new XmlReaderSettings
        {
            Async = true,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = RequestBodySizeGuard.ResolveMaxBodyBytes(context),
            MaxCharactersFromEntities = 0,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true
        };
        using var reader = XmlReader.Create(context.Request.Body, settings);
        var document = await XDocument.LoadAsync(reader, LoadOptions.None, context.RequestAborted).ConfigureAwait(false);
        var root = document.Root ?? throw new WpsRequestException("MissingParameterValue", "The request document is empty.", "request");
        if (root.Name.NamespaceName != WpsNamespace)
        {
            throw new WpsRequestException("InvalidParameterValue", "The request root must use the WPS 2.0 namespace.", "request");
        }
        ValidateBindingValue(root.Attribute("service")?.Value, "WPS", "service");
        ValidateBindingValue(root.Attribute("version")?.Value, Version, "version");
        var operation = root.Name.LocalName;
        var identifier = root.Descendants(XName.Get("Identifier", OwsNamespace)).FirstOrDefault()?.Value.Trim();
        var jobId = root.Descendants(XName.Get("JobID", WpsNamespace)).FirstOrDefault()?.Value.Trim();
        var inputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var input in root.Elements(XName.Get("Input", WpsNamespace)))
        {
            if (inputs.Count >= MaxInputs)
            {
                throw new WpsRequestException("InvalidParameterValue", $"At most {MaxInputs} inputs are allowed.", "input");
            }
            var name = input.Attribute("id")?.Value.Trim();
            var value = input.Descendants(XName.Get("LiteralValue", WpsNamespace)).FirstOrDefault()?.Value;
            AddInput(inputs, name, value);
        }
        return new WpsRequest(operation, identifier, jobId, inputs);
    }

    private static WpsRequest FromValues(Dictionary<string, string> values)
    {
        values.TryGetValue("service", out var service);
        values.TryGetValue("version", out var version);
        ValidateBindingValue(service, "WPS", "service");
        ValidateBindingValue(version, Version, "version");
        if (!values.TryGetValue("request", out var operation) || string.IsNullOrWhiteSpace(operation))
        {
            throw new WpsRequestException("MissingParameterValue", "The request parameter is required.", "request");
        }
        values.TryGetValue("identifier", out var identifier);
        values.TryGetValue("jobId", out var jobId);
        var inputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (values.TryGetValue("dataInputs", out var dataInputs) && !string.IsNullOrWhiteSpace(dataInputs))
        {
            foreach (var item in dataInputs.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var separator = item.IndexOf('=');
                if (separator <= 0)
                {
                    throw new WpsRequestException("InvalidParameterValue", "DataInputs must use name=value pairs separated by semicolons.", "dataInputs");
                }
                AddInput(inputs, item[..separator], item[(separator + 1)..]);
            }
        }
        return new WpsRequest(operation.Trim(), identifier?.Trim(), jobId?.Trim(), inputs);
    }

    private static void ValidateBindingValue(string? actual, string expected, string locator)
    {
        if (string.IsNullOrWhiteSpace(actual))
        {
            throw new WpsRequestException("MissingParameterValue", $"The {locator} value is required.", locator);
        }
        if (!string.Equals(actual.Trim(), expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new WpsRequestException("InvalidParameterValue", $"The {locator} value must be '{expected}'.", locator);
        }
    }

    private static void AddInput(Dictionary<string, string> inputs, string? name, string? value)
    {
        if (string.IsNullOrWhiteSpace(name) || value is null || value.Length > MaxInputCharacters || inputs.Count >= MaxInputs)
        {
            throw new WpsRequestException("InvalidParameterValue", "Each input requires a bounded identifier and literal value.", "input");
        }
        if (!inputs.TryAdd(name.Trim(), value))
        {
            throw new WpsRequestException("InvalidParameterValue", $"Input '{name}' is repeated.", name);
        }
    }

    private static string Operation(string name, string endpoint) =>
        $"<ows:Operation name=\"{name}\"><ows:DCP><ows:HTTP><ows:Get xmlns:xlink=\"http://www.w3.org/1999/xlink\" xlink:href=\"{endpoint}\"/><ows:Post xmlns:xlink=\"http://www.w3.org/1999/xlink\" xlink:href=\"{endpoint}\"/></ows:HTTP></ows:DCP></ows:Operation>";

    private static IResult Exception(string code, string message, string? locator = null, int statusCode = StatusCodes.Status400BadRequest)
    {
        var locatorAttribute = locator is null ? string.Empty : $" locator=\"{X(locator)}\"";
        return Xml($"<?xml version=\"1.0\" encoding=\"UTF-8\"?><ows:ExceptionReport xmlns:ows=\"{OwsNamespace}\" version=\"{Version}\"><ows:Exception exceptionCode=\"{X(code)}\"{locatorAttribute}><ows:ExceptionText>{X(message)}</ows:ExceptionText></ows:Exception></ows:ExceptionReport>", statusCode);
    }

    private static IResult Xml(string content, int statusCode = StatusCodes.Status200OK) =>
        Results.Content(content, "application/xml", Encoding.UTF8, statusCode);

    private static string X(string value) => OgcClassicRequestHelpers.EscapeXml(value);

    private sealed record WpsRequest(string Operation, string? Identifier, string? JobId, IReadOnlyDictionary<string, string> Inputs);

    private sealed class WpsRequestException(string code, string message, string? locator, int statusCode = StatusCodes.Status400BadRequest) : Exception(message)
    {
        public string Code { get; } = code;
        public string? Locator { get; } = locator;
        public int StatusCode { get; } = statusCode;
    }

    internal sealed class Wps20EndpointLog;

    private static partial class Log
    {
        [LoggerMessage(7101, LogLevel.Debug, "WPS operation {Operation} requested")]
        public static partial void OperationRequested(ILogger logger, string operation);

        [LoggerMessage(7102, LogLevel.Warning, "Invalid WPS request: {Reason}")]
        public static partial void InvalidRequest(ILogger logger, string reason);
    }
}
