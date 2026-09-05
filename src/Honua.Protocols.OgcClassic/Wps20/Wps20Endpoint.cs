// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Middleware;
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
    private const int MaxOutputs = 1;
    private const int MaxInputCharacters = 65_536;
    private static readonly string[] Methods = ["GET", "POST"];

    internal static IEndpointRouteBuilder MapWps20Endpoint(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapMethods("/wps", Methods, DispatchAsync)
            .WithMetadata(new HeadRequestRejectedEndpointMetadata(Methods, ShouldRejectHead))
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

        endpoints.MapGet("/wps/conformance/results/{token}", GetConformanceResultReference)
            .WithDisplayName("WPS conformance result reference")
            .WithName("Wps20ConformanceResult")
            .ExcludeFromDescription()
            .AllowAnonymous();

        return endpoints;
    }

    /// <summary>
    /// Execute is the only WPS KVP operation that creates durable work. The other dispatcher
    /// operations are discovery or job-result reads and retain ordinary GET-equivalent HEAD
    /// behavior.
    /// </summary>
    private static bool ShouldRejectHead(HttpContext context)
    {
        var operation = context.Request.Query["request"].ToString().Trim();
        return string.Equals(operation, "Execute", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<IResult> DispatchAsync(
        HttpContext context,
        IProcessCatalog catalog,
        IGeoprocessingJobService jobs,
        Wps20ConformanceEcho echo,
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
        catch (XmlSchemaException ex)
        {
            Log.InvalidRequest(logger, ex.Message);
            return Exception("InvalidParameterValue", "The XML request is not valid or contains prohibited constructs.", "request");
        }

        using var activity = HonuaTelemetry.ActivitySource.StartActivity("ogc.wps." + request.Operation.ToLowerInvariant());
        activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.Wps20);
        activity?.SetTag(HonuaTelemetry.Tags.Operation, request.Operation);
        if (request.JobId is not null)
        {
            activity?.SetTag(HonuaTelemetry.Tags.JobId, request.JobId);
        }
        context.Items[RequestTelemetryClassifier.OperationItemKey] = "wps." + request.Operation.ToLowerInvariant();
        Log.OperationRequested(logger, request.Operation);

        try
        {
            return request.Operation.ToUpperInvariant() switch
            {
                "GETCAPABILITIES" => GetCapabilities(context, catalog, echo),
                "DESCRIBEPROCESS" => DescribeProcess(catalog, echo, request.Identifier),
                "EXECUTE" => await ExecuteAsync(context, catalog, jobs, echo, request).ConfigureAwait(false),
                "GETSTATUS" => await GetStatusAsync(context, jobs, echo, request.JobId).ConfigureAwait(false),
                "GETRESULT" => await GetResultAsync(context, jobs, echo, request.JobId).ConfigureAwait(false),
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
        catch (GeoprocessingStoreUnavailableException storeEx)
        {
            // honua-release#202: project the capability-unavailable receipt into the OWS exception
            // report. The exceptionCode stays "ServerBusy" because OWS constrains that vocabulary
            // and CITE checks it; the machine-readable fields ride additional ows:ExceptionText
            // elements as stable "key: value" lines, the same convention the GeoServices
            // error.details[] projection uses.
            return Exception(
                "ServerBusy",
                storeEx.Message,
                null,
                StatusCodes.Status503ServiceUnavailable,
                BuildCapabilityUnavailableTexts(storeEx));
        }
        catch (Wps20EchoException ex)
        {
            return Exception("InvalidParameterValue", ex.Message, "input");
        }
    }

    private static IResult GetCapabilities(HttpContext context, IProcessCatalog catalog, Wps20ConformanceEcho echo)
    {
        var endpoint = OgcClassicRequestHelpers.EscapeXml(echo.BuildPublicUrl(context, "/wps"));
        var offerings = string.Join(string.Empty, catalog.ListProcesses()
            .Where(ProcessExecutionEligibility.IsJobCallable)
            .OrderBy(p => p.ProcessId, StringComparer.Ordinal).Select(p =>
            $"<wps:ProcessSummary processVersion=\"1.0.0\" jobControlOptions=\"async-execute\" outputTransmission=\"value\"><ows:Title>{X(p.Title)}</ows:Title><ows:Identifier>{X(p.ProcessId)}</ows:Identifier></wps:ProcessSummary>"));
        if (echo.Enabled)
        {
            offerings += $"<wps:ProcessSummary processVersion=\"1.0.0\" jobControlOptions=\"sync-execute async-execute\" outputTransmission=\"value reference\"><ows:Title>Honua CITE echo</ows:Title><ows:Identifier>{X(echo.ProcessId)}</ows:Identifier></wps:ProcessSummary>";
        }
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

    private static IResult DescribeProcess(IProcessCatalog catalog, Wps20ConformanceEcho echo, string? identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return Exception("MissingParameterValue", "An Identifier is required.", "identifier");
        }

        var descriptions = new StringBuilder();
        if (string.Equals(identifier, "ALL", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var process in catalog.ListProcesses()
                         .Where(ProcessExecutionEligibility.IsJobCallable)
                         .OrderBy(process => process.ProcessId, StringComparer.Ordinal))
            {
                descriptions.Append(DescribeCanonicalProcess(process));
            }
            if (echo.Enabled)
            {
                descriptions.Append(DescribeEchoProcess(echo.ProcessId));
            }
        }
        else if (echo.IsEchoProcess(identifier))
        {
            descriptions.Append(DescribeEchoProcess(identifier));
        }
        else if (catalog.GetProcess(identifier) is { } process
                 && ProcessExecutionEligibility.IsJobCallable(process))
        {
            descriptions.Append(DescribeCanonicalProcess(process));
        }
        else
        {
            return Exception("NoSuchProcess", $"Process '{identifier}' does not exist.", "identifier", StatusCodes.Status404NotFound);
        }

        return Xml($"""
            <?xml version="1.0" encoding="UTF-8"?>
            <wps:ProcessOfferings xmlns:wps="{WpsNamespace}" xmlns:ows="{OwsNamespace}">{descriptions}</wps:ProcessOfferings>
            """);
    }

    private static string DescribeCanonicalProcess(ProcessDefinition process)
    {
        var inputs = string.Join(string.Empty, process.Parameters.Select(parameter =>
            $"<wps:Input minOccurs=\"{(parameter.Required ? "1" : "0")}\" maxOccurs=\"1\"><ows:Title>{X(parameter.DisplayName)}</ows:Title><ows:Abstract>{X(parameter.Description)}</ows:Abstract><ows:Identifier>{X(parameter.Name)}</ows:Identifier><wps:LiteralData><wps:Format mimeType=\"text/plain\" default=\"true\"/>{LiteralDataDomain(parameter.ValueType)}</wps:LiteralData></wps:Input>"));
        var outputs = DescribeCanonicalOutputs(process.OutputArtifactKinds);
        var transmission = process.OutputArtifactKinds.Count == 0 ? "value" : "value reference";
        return $"<wps:ProcessOffering processVersion=\"1.0.0\" jobControlOptions=\"async-execute\" outputTransmission=\"{transmission}\"><wps:Process><ows:Title>{X(process.Title)}</ows:Title><ows:Abstract>{X(process.Description)}</ows:Abstract><ows:Identifier>{X(process.ProcessId)}</ows:Identifier>{inputs}{outputs}</wps:Process></wps:ProcessOffering>";
    }

    private static string DescribeCanonicalOutputs(IReadOnlyList<ArtifactKind> outputKinds)
    {
        if (outputKinds.Count == 0)
        {
            return $"<wps:Output><ows:Title>Result summary</ows:Title><ows:Identifier>result</ows:Identifier><wps:LiteralData><wps:Format mimeType=\"text/plain\" default=\"true\"/>{LiteralDataDomain(ProcessParameterValueType.Text)}</wps:LiteralData></wps:Output>";
        }

        var ordinals = new Dictionary<ArtifactKind, int>();
        return string.Join(string.Empty, outputKinds.Select(kind =>
        {
            var ordinal = ordinals.TryGetValue(kind, out var previous) ? previous + 1 : 1;
            ordinals[kind] = ordinal;
            var identifier = ResolveOutputIdentifier(kind, ordinal);
            var title = $"{kind} output";
            return kind == ArtifactKind.Scalar
                ? $"<wps:Output><ows:Title>{X(title)}</ows:Title><ows:Identifier>{identifier}</ows:Identifier><wps:LiteralData><wps:Format mimeType=\"text/plain\" default=\"true\"/>{LiteralDataDomain(ProcessParameterValueType.Text)}</wps:LiteralData></wps:Output>"
                : $"<wps:Output><ows:Title>{X(title)}</ows:Title><ows:Identifier>{identifier}</ows:Identifier><wps:ComplexData><wps:Format mimeType=\"{DefaultArtifactContentType(kind)}\" default=\"true\"/></wps:ComplexData></wps:Output>";
        }));
    }

    private static string LiteralDataDomain(ProcessParameterValueType valueType)
    {
        var (reference, name) = valueType switch
        {
            ProcessParameterValueType.WholeNumber or ProcessParameterValueType.Srid =>
                ("http://www.w3.org/2001/XMLSchema#integer", "integer"),
            ProcessParameterValueType.FloatingPoint =>
                ("http://www.w3.org/2001/XMLSchema#double", "double"),
            ProcessParameterValueType.Flag =>
                ("http://www.w3.org/2001/XMLSchema#boolean", "boolean"),
            _ => ("http://www.w3.org/2001/XMLSchema#string", "string")
        };
        return $"<LiteralDataDomain default=\"true\"><ows:AnyValue/><ows:DataType ows:reference=\"{reference}\">{name}</ows:DataType></LiteralDataDomain>";
    }

    private static string DescribeEchoProcess(string processId) => $"""
        <wps:ProcessOffering processVersion="1.0.0" jobControlOptions="sync-execute async-execute" outputTransmission="value reference">
          <wps:Process>
            <ows:Title>Honua CITE echo</ows:Title><ows:Abstract>Returns one bounded input without geospatial processing.</ows:Abstract><ows:Identifier>{X(processId)}</ows:Identifier>
            <wps:Input minOccurs="0" maxOccurs="1"><ows:Title>Literal input</ows:Title><ows:Identifier>literalInput</ows:Identifier><wps:LiteralData><wps:Format mimeType="text/plain" default="true"/><LiteralDataDomain default="true"><ows:AnyValue/><ows:DataType ows:reference="http://www.w3.org/2001/XMLSchema#string">string</ows:DataType></LiteralDataDomain></wps:LiteralData></wps:Input>
            <wps:Input minOccurs="0" maxOccurs="1"><ows:Title>Complex input</ows:Title><ows:Identifier>complexInput</ows:Identifier><wps:ComplexData><wps:Format mimeType="text/xml" default="true"/></wps:ComplexData></wps:Input>
            <wps:Input minOccurs="0" maxOccurs="1"><ows:Title>Bounding box input</ows:Title><ows:Identifier>boundingBoxInput</ows:Identifier><wps:BoundingBoxData><wps:Format mimeType="text/xml" default="true"/><wps:SupportedCRS default="true">urn:ogc:def:crs:OGC:1.3:CRS84</wps:SupportedCRS></wps:BoundingBoxData></wps:Input>
            <wps:Output><ows:Title>Literal output</ows:Title><ows:Identifier>literalOutput</ows:Identifier><wps:LiteralData><wps:Format mimeType="text/plain" default="true"/><LiteralDataDomain default="true"><ows:AnyValue/><ows:DataType ows:reference="http://www.w3.org/2001/XMLSchema#string">string</ows:DataType></LiteralDataDomain></wps:LiteralData></wps:Output>
            <wps:Output><ows:Title>Complex output</ows:Title><ows:Identifier>complexOutput</ows:Identifier><wps:ComplexData><wps:Format mimeType="text/xml" default="true"/></wps:ComplexData></wps:Output>
            <wps:Output><ows:Title>Bounding box output</ows:Title><ows:Identifier>boundingBoxOutput</ows:Identifier><wps:BoundingBoxData><wps:Format mimeType="text/xml" default="true"/><wps:SupportedCRS default="true">urn:ogc:def:crs:OGC:1.3:CRS84</wps:SupportedCRS></wps:BoundingBoxData></wps:Output>
          </wps:Process>
        </wps:ProcessOffering>
        """;

    private static async Task<IResult> ExecuteAsync(HttpContext context, IProcessCatalog catalog, IGeoprocessingJobService jobs, Wps20ConformanceEcho echo, WpsRequest request)
    {
        if (echo.IsEchoProcess(request.Identifier))
        {
            return await ExecuteEchoAsync(context, echo, request).ConfigureAwait(false);
        }
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

        var canonicalContractError = ValidateCanonicalExecuteContract(request, process);
        if (canonicalContractError is not null)
        {
            return canonicalContractError;
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
            .ToDictionary(p => p.Name, p => request.Inputs.GetValueOrDefault(p.Name)?.Value ?? p.DefaultValue!, StringComparer.Ordinal);
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
        var location = echo.BuildPublicUrl(context, $"/wps?service=WPS&request=GetStatus&version={Version}&jobId={Uri.EscapeDataString(job.OperationId)}");
        context.Response.Headers.Location = location;
        return StatusInfo(job, StatusCodes.Status201Created);
    }

    private static async Task<IResult> ExecuteEchoAsync(HttpContext context, Wps20ConformanceEcho echo, WpsRequest request)
    {
        if (request.Inputs.Count != 1)
        {
            return Exception("InvalidParameterValue", "The conformance echo requires exactly one input.", "input");
        }
        var input = request.Inputs.Values.Single();
        var output = request.Outputs.SingleOrDefault();
        var outputId = output?.Id ?? input.Kind switch
        {
            EchoValueKind.Complex => "complexOutput",
            EchoValueKind.BoundingBox => "boundingBoxOutput",
            _ => "literalOutput"
        };
        var transmission = output?.Transmission ?? "value";
        var allowedOutputId = input.Kind switch
        {
            EchoValueKind.Complex => "complexOutput",
            EchoValueKind.BoundingBox => "boundingBoxOutput",
            _ => "literalOutput"
        };
        if (!string.Equals(outputId, allowedOutputId, StringComparison.Ordinal)
            || transmission is not ("value" or "reference"))
        {
            return Exception("InvalidParameterValue", "The requested echo output identifier or transmission is not supported.", "output");
        }
        var mode = request.Mode ?? "sync";
        var responseForm = request.ResponseForm ?? "document";
        if (mode is not ("sync" or "async"))
        {
            return Exception("InvalidParameterValue", "Echo Execute mode must be 'sync' or 'async'.", "mode");
        }
        if (responseForm is not ("document" or "raw"))
        {
            return Exception("InvalidParameterValue", "Echo Execute response must be 'document' or 'raw'.", "response");
        }

        var value = await echo.ResolveInputAsync(input, context.RequestAborted).ConfigureAwait(false);
        if (string.Equals(mode, "async", StringComparison.OrdinalIgnoreCase))
        {
            var jobId = echo.Store(value, outputId, transmission, responseForm);
            return EchoStatusInfo(jobId);
        }
        if (string.Equals(responseForm, "raw", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Content(value.Value, value.MimeType ?? "text/plain", Encoding.UTF8);
        }
        return EchoResult(context, echo, value, outputId, transmission, null);
    }

    private static IResult? ValidateCanonicalExecuteContract(WpsRequest request, ProcessDefinition process)
    {
        var mode = request.Mode ?? "async";
        var responseForm = request.ResponseForm ?? "document";
        if (!string.Equals(mode, "async", StringComparison.OrdinalIgnoreCase))
        {
            return Exception("InvalidParameterValue", "Canonical WPS processes support only mode='async'.", "mode");
        }
        if (!string.Equals(responseForm, "document", StringComparison.OrdinalIgnoreCase))
        {
            return Exception("InvalidParameterValue", "Canonical WPS processes support only response='document'.", "response");
        }
        var output = request.Outputs.SingleOrDefault();
        if (output is not null)
        {
            var allowedIdentifiers = process.OutputArtifactKinds.Count == 0
                ? new[] { "result" }
                : DescribeOutputIdentifiers(process.OutputArtifactKinds);
            if (!allowedIdentifiers.Contains(output.Id, StringComparer.Ordinal) ||
                (process.OutputArtifactKinds.Count == 0 && !string.Equals(output.Transmission, "value", StringComparison.OrdinalIgnoreCase)))
            {
                return Exception("InvalidParameterValue", "The requested canonical WPS output is not advertised by this process.", "output");
            }
        }
        return null;
    }

    private static string[] DescribeOutputIdentifiers(IReadOnlyList<ArtifactKind> outputKinds)
    {
        var ordinals = new Dictionary<ArtifactKind, int>();
        return outputKinds.Select(kind =>
        {
            var ordinal = ordinals.TryGetValue(kind, out var previous) ? previous + 1 : 1;
            ordinals[kind] = ordinal;
            return ResolveOutputIdentifier(kind, ordinal);
        }).ToArray();
    }

    private static async Task<IResult> GetStatusAsync(HttpContext context, IGeoprocessingJobService jobs, Wps20ConformanceEcho echo, string? jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            return Exception("MissingParameterValue", "A JobID is required.", "jobId");
        }
        if (echo.TryGet(jobId, out _))
        {
            return EchoStatusInfo(jobId);
        }
        var job = await jobs.GetJobAsync(jobId, context.User, context.RequestAborted).ConfigureAwait(false);
        return StatusInfo(job);
    }

    private static async Task<IResult> GetResultAsync(HttpContext context, IGeoprocessingJobService jobs, Wps20ConformanceEcho echo, string? jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            return Exception("MissingParameterValue", "A JobID is required.", "jobId");
        }
        if (echo.TryGet(jobId, out var stored))
        {
            return EchoResult(context, echo, stored.Value, stored.OutputId, stored.Transmission, jobId);
        }
        var package = await jobs.GetJobResultsAsync(jobId, context.User, context.RequestAborted).ConfigureAwait(false);
        return Xml(BuildJobResultXml(jobId, package));
    }

    private static string BuildJobResultXml(string jobId, AnalysisResultPackage package)
    {
        var outputs = new StringBuilder();
        if (package.Artifacts.Count == 0)
        {
            outputs.Append($"<wps:Output><ows:Identifier>result</ows:Identifier><wps:Data mimeType=\"text/plain\"><wps:LiteralValue>{X(package.Summary.Title)}</wps:LiteralValue></wps:Data></wps:Output>");
        }
        else
        {
            var ordinals = new Dictionary<ArtifactKind, int>();
            for (var index = 0; index < package.Artifacts.Count; index++)
            {
                var artifact = package.Artifacts[index];
                var ordinal = ordinals.TryGetValue(artifact.Kind, out var previous) ? previous + 1 : 1;
                ordinals[artifact.Kind] = ordinal;
                var identifier = ResolveOutputIdentifier(artifact.Kind, ordinal);
                var contentType = artifact.ContentType ?? DefaultArtifactContentType(artifact.Kind);
                var artifactUri = artifact.Uri;
                if (!string.IsNullOrWhiteSpace(artifactUri))
                {
                    outputs.Append($"<wps:Output><ows:Identifier>{X(identifier)}</ows:Identifier><wps:Reference xmlns:xlink=\"http://www.w3.org/1999/xlink\" mimeType=\"{X(contentType)}\" xlink:href=\"{X(artifactUri)}\"/></wps:Output>");
                }
                else
                {
                    outputs.Append($"<wps:Output><ows:Identifier>{X(identifier)}</ows:Identifier><wps:Data mimeType=\"text/plain\"><wps:LiteralValue>{X(artifact.Label)}</wps:LiteralValue></wps:Data></wps:Output>");
                }
            }
        }

        return $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><wps:Result xmlns:wps=\"{WpsNamespace}\" xmlns:ows=\"{OwsNamespace}\" jobID=\"{X(jobId)}\">{outputs}</wps:Result>";
    }

    private static string ResolveOutputIdentifier(ArtifactKind kind, int ordinal)
    {
        var baseIdentifier = kind switch
        {
            ArtifactKind.FeatureLayer => "featureLayer",
            ArtifactKind.Table => "table",
            ArtifactKind.Raster => "raster",
            ArtifactKind.File => "file",
            ArtifactKind.Report => "report",
            ArtifactKind.Map => "map",
            ArtifactKind.Scalar => "scalar",
            ArtifactKind.AppBundle => "bundle",
            _ => "artifact"
        };
        return $"{baseIdentifier}{ordinal.ToString(CultureInfo.InvariantCulture)}";
    }

    private static string DefaultArtifactContentType(ArtifactKind kind)
        => kind switch
        {
            ArtifactKind.FeatureLayer => "application/geo+json",
            ArtifactKind.Table => "text/csv",
            ArtifactKind.Raster => "image/tiff",
            ArtifactKind.Report => "application/pdf",
            ArtifactKind.Map => "image/png",
            ArtifactKind.File => "application/octet-stream",
            ArtifactKind.AppBundle => "application/zip",
            ArtifactKind.Scalar => "text/plain",
            _ => "application/octet-stream"
        };

    private static IResult EchoStatusInfo(string jobId) =>
        Xml($"<?xml version=\"1.0\" encoding=\"UTF-8\"?><wps:StatusInfo xmlns:wps=\"{WpsNamespace}\"><wps:JobID>{X(jobId)}</wps:JobID><wps:Status>Succeeded</wps:Status><wps:PercentCompleted>100</wps:PercentCompleted></wps:StatusInfo>");

    private static IResult EchoResult(HttpContext context, Wps20ConformanceEcho echo, EchoValue value, string outputId, string transmission, string? jobId)
    {
        string body;
        if (string.Equals(transmission, "reference", StringComparison.OrdinalIgnoreCase))
        {
            var token = echo.Store(value, outputId, "value", "raw");
            var href = X(echo.BuildPublicUrl(context, $"/wps/conformance/results/{token}"));
            body = $"<wps:Reference xmlns:xlink=\"http://www.w3.org/1999/xlink\" mimeType=\"{X(value.MimeType ?? "text/plain")}\" xlink:href=\"{href}\"/>";
        }
        else if (value.Kind is EchoValueKind.Complex or EchoValueKind.BoundingBox)
        {
            body = $"<wps:Data mimeType=\"{X(value.MimeType ?? "text/xml")}\">{value.Value}</wps:Data>";
        }
        else
        {
            body = $"<wps:Data mimeType=\"{X(value.MimeType ?? "text/plain")}\"><wps:LiteralValue>{X(value.Value)}</wps:LiteralValue></wps:Data>";
        }
        var jobAttribute = jobId is null ? string.Empty : $" jobID=\"{X(jobId)}\"";
        return Xml($"<?xml version=\"1.0\" encoding=\"UTF-8\"?><wps:Result xmlns:wps=\"{WpsNamespace}\" xmlns:ows=\"{OwsNamespace}\"{jobAttribute}><wps:Output><ows:Identifier>{X(outputId)}</ows:Identifier>{body}</wps:Output></wps:Result>");
    }

    private static IResult GetConformanceResultReference(string token, Wps20ConformanceEcho echo)
    {
        if (!echo.Enabled || !echo.TryGet(token, out var stored))
        {
            return Results.NotFound();
        }
        return Results.Content(stored.Value.Value, stored.Value.MimeType ?? "text/plain", Encoding.UTF8);
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
        // Keep these assignments explicit: the CodeQL XmlReaderSettings model traces
        // property setters from the settings creation to the XmlReader.Create call.
        settings.ValidationType = ValidationType.Schema;
        settings.Schemas = Wps20RequestSchema.SchemaSet;
        settings.ValidationFlags = XmlSchemaValidationFlags.None;
        using var reader = XmlReader.Create(context.Request.Body, settings);
        var document = await XDocument.LoadAsync(reader, LoadOptions.None, context.RequestAborted).ConfigureAwait(false);
        var root = document.Root ?? throw new WpsRequestException("MissingParameterValue", "The request document is empty.", "request");
        if (root.Name.NamespaceName != WpsNamespace)
        {
            throw new WpsRequestException("InvalidParameterValue", "The request root must use the WPS 2.0 namespace.", "request");
        }
        var operation = root.Name.LocalName;
        ValidateBindingValue(root.Attribute("service")?.Value, "WPS", "service");
        var requestedVersion = root.Attribute("version")?.Value;
        if (!string.Equals(operation, "GetCapabilities", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrWhiteSpace(requestedVersion))
        {
            ValidateBindingValue(requestedVersion, Version, "version");
        }
        var identifier = root.Descendants(XName.Get("Identifier", OwsNamespace)).FirstOrDefault()?.Value.Trim();
        var jobId = root.Descendants(XName.Get("JobID", WpsNamespace)).FirstOrDefault()?.Value.Trim();
        var inputs = new Dictionary<string, EchoInput>(StringComparer.OrdinalIgnoreCase);
        foreach (var input in root.Elements(XName.Get("Input", WpsNamespace)))
        {
            if (inputs.Count >= MaxInputs)
            {
                throw new WpsRequestException("InvalidParameterValue", $"At most {MaxInputs} inputs are allowed.", "input");
            }
            var name = input.Attribute("id")?.Value.Trim();
            AddXmlInput(inputs, input, name);
        }
        var outputs = root.Elements(XName.Get("Output", WpsNamespace))
            .Select(output => new WpsOutput(output.Attribute("id")?.Value.Trim() ?? string.Empty, output.Attribute("transmission")?.Value.Trim() ?? "value"))
            .ToArray();
        if (outputs.Length > MaxOutputs)
        {
            throw new WpsRequestException("InvalidParameterValue", $"At most {MaxOutputs} output may be requested.", "output");
        }
        if (outputs.Any(output => string.IsNullOrWhiteSpace(output.Id)
            || output.Transmission is not ("value" or "reference")))
        {
            throw new WpsRequestException("InvalidParameterValue", "Each output requires an identifier and a supported transmission.", "output");
        }
        return new WpsRequest(operation, identifier, jobId, inputs, outputs, root.Attribute("mode")?.Value.Trim(), root.Attribute("response")?.Value.Trim());
    }

    private static WpsRequest FromValues(Dictionary<string, string> values)
    {
        values.TryGetValue("service", out var service);
        values.TryGetValue("version", out var version);
        ValidateBindingValue(service, "WPS", "service");
        if (!values.TryGetValue("request", out var operation) || string.IsNullOrWhiteSpace(operation))
        {
            throw new WpsRequestException("MissingParameterValue", "The request parameter is required.", "request");
        }
        if (string.Equals(operation.Trim(), "GetCapabilities", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(version))
            {
                ValidateBindingValue(version, Version, "version");
            }

            if (values.TryGetValue("acceptVersions", out var acceptVersions) &&
                !string.IsNullOrWhiteSpace(acceptVersions) &&
                !acceptVersions.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Any(candidate => string.Equals(candidate, Version, StringComparison.OrdinalIgnoreCase)))
            {
                throw new WpsRequestException("VersionNegotiationFailed", $"The AcceptVersions value does not include supported WPS version '{Version}'.", "acceptVersions");
            }
        }
        else
        {
            ValidateBindingValue(version, Version, "version");
        }
        values.TryGetValue("identifier", out var identifier);
        values.TryGetValue("jobId", out var jobId);
        var inputs = new Dictionary<string, EchoInput>(StringComparer.OrdinalIgnoreCase);
        if (values.TryGetValue("dataInputs", out var dataInputs) && !string.IsNullOrWhiteSpace(dataInputs))
        {
            foreach (var item in dataInputs.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var separator = item.IndexOf('=');
                if (separator <= 0)
                {
                    throw new WpsRequestException("InvalidParameterValue", "DataInputs must use name=value pairs separated by semicolons.", "dataInputs");
                }
                AddInput(inputs, item[..separator], new EchoInput(item[..separator], EchoValueKind.Literal, item[(separator + 1)..], "text/plain"));
            }
        }
        return new WpsRequest(operation.Trim(), identifier?.Trim(), jobId?.Trim(), inputs, [], null, null);
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

    private static void AddXmlInput(Dictionary<string, EchoInput> inputs, XElement input, string? name)
    {
        var literal = input.Descendants(XName.Get("LiteralValue", WpsNamespace)).FirstOrDefault();
        if (literal is not null)
        {
            AddInput(inputs, name, new EchoInput(name ?? string.Empty, EchoValueKind.Literal, literal.Value, "text/plain"));
            return;
        }
        var reference = input.Element(XName.Get("Reference", WpsNamespace));
        if (reference is not null)
        {
            XNamespace xlink = "http://www.w3.org/1999/xlink";
            AddInput(inputs, name, new EchoInput(name ?? string.Empty, EchoValueKind.Reference, reference.Attribute(xlink + "href")?.Value ?? string.Empty, reference.Attribute("mimeType")?.Value));
            return;
        }
        var data = input.Element(XName.Get("Data", WpsNamespace));
        if (data is not null)
        {
            var value = string.Concat(data.Nodes().Select(node => node.ToString(SaveOptions.DisableFormatting)));
            AddInput(inputs, name, new EchoInput(name ?? string.Empty, EchoValueKind.Complex, value, "text/xml"));
            return;
        }
        throw new WpsRequestException("InvalidParameterValue", "Each input requires data or a reference.", name ?? "input");
    }

    private static void AddInput(Dictionary<string, EchoInput> inputs, string? name, EchoInput input)
    {
        if (string.IsNullOrWhiteSpace(name) || input.Value.Length > MaxInputCharacters || inputs.Count >= MaxInputs)
        {
            throw new WpsRequestException("InvalidParameterValue", "Each input requires a bounded identifier and literal value.", "input");
        }
        if (!inputs.TryAdd(name.Trim(), input))
        {
            throw new WpsRequestException("InvalidParameterValue", $"Input '{name}' is repeated.", name);
        }
    }

    private static string Operation(string name, string endpoint) =>
        $"<ows:Operation name=\"{name}\"><ows:DCP><ows:HTTP><ows:Get xmlns:xlink=\"http://www.w3.org/1999/xlink\" xlink:href=\"{endpoint}\"/><ows:Post xmlns:xlink=\"http://www.w3.org/1999/xlink\" xlink:href=\"{endpoint}\"/></ows:HTTP></ows:DCP></ows:Operation>";

    private static IResult Exception(
        string code,
        string message,
        string? locator = null,
        int statusCode = StatusCodes.Status400BadRequest,
        IReadOnlyList<string>? additionalTexts = null)
    {
        var locatorAttribute = locator is null ? string.Empty : $" locator=\"{X(locator)}\"";
        var extraTexts = additionalTexts is { Count: > 0 }
            ? string.Concat(additionalTexts.Select(text => $"<ows:ExceptionText>{X(text)}</ows:ExceptionText>"))
            : string.Empty;
        return Xml($"<?xml version=\"1.0\" encoding=\"UTF-8\"?><ows:ExceptionReport xmlns:ows=\"{OwsNamespace}\" version=\"{Version}\"><ows:Exception exceptionCode=\"{X(code)}\"{locatorAttribute}><ows:ExceptionText>{X(message)}</ows:ExceptionText>{extraTexts}</ows:Exception></ows:ExceptionReport>", statusCode);
    }

    /// <summary>
    /// The capability-unavailable receipt (honua-release#202) as additional
    /// <c>ows:ExceptionText</c> lines. OWS 1.1 permits repeated ExceptionText elements, so this
    /// adds machine-readable fields without inventing an exceptionCode outside the OWS vocabulary.
    /// Returns <see langword="null"/> when the exception carries no receipt.
    /// </summary>
    private static List<string>? BuildCapabilityUnavailableTexts(
        GeoprocessingStoreUnavailableException exception)
    {
        if (!exception.HasDependencyReceipt)
        {
            return null;
        }

        var texts = new List<string>(5) { $"code: {exception.ErrorCode}" };
        if (exception.MissingDependency is not null)
        {
            texts.Add($"missingDependency: {exception.MissingDependency}");
        }

        if (exception.MissingEntitlement is not null)
        {
            texts.Add($"missingEntitlement: {exception.MissingEntitlement}");
        }

        texts.Add($"capability: {exception.CapabilityId}");
        texts.Add($"remediation: {exception.Remediation}");
        texts.Add($"remediationRef: {exception.RemediationRef}");
        return texts;
    }

    private static IResult Xml(string content, int statusCode = StatusCodes.Status200OK) =>
        Results.Content(content, "application/xml", Encoding.UTF8, statusCode);

    private static string X(string value) => OgcClassicRequestHelpers.EscapeXml(value);

    private sealed record WpsRequest(
        string Operation,
        string? Identifier,
        string? JobId,
        IReadOnlyDictionary<string, EchoInput> Inputs,
        IReadOnlyList<WpsOutput> Outputs,
        string? Mode,
        string? ResponseForm);

    private sealed record WpsOutput(string Id, string Transmission);

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
