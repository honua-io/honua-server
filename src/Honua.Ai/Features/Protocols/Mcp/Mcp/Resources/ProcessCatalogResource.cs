// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing;
using Honua.Server.Features.Protocols.Mcp.Models;

namespace Honua.Server.Features.Protocols.Mcp.Resources;

/// <summary>
/// MCP resource for <c>honua://catalog/processes</c>. Projects the canonical
/// built-in process catalog into the MCP inspection contract used by app
/// builders and operators.
/// </summary>
internal sealed class ProcessCatalogResource : IMcpResource
{
    public const string Uri = McpResourceUris.CatalogProcesses;

    private const string EmptyCatalogReason =
        "No process catalog entries are available from the configured catalog provider.";

    private readonly IGeoprocessingJobService _jobService;
    private readonly IProcessCatalog _processCatalog;
    private readonly ILogger<ProcessCatalogResource> _logger;

    public ProcessCatalogResource(
        IGeoprocessingJobService jobService,
        IProcessCatalog processCatalog,
        ILogger<ProcessCatalogResource> logger)
    {
        _jobService = jobService;
        _processCatalog = processCatalog;
        _logger = logger;
    }

    public ProcessCatalogResource(IGeoprocessingJobService jobService, ILogger<ProcessCatalogResource> logger)
        : this(jobService, new BuiltInProcessCatalog(), logger)
    {
    }

    public string Family => McpTelemetry.ResourceFamily.Catalog;

    public IReadOnlyList<McpResourceDescriptor> Describe() => new[]
    {
        new McpResourceDescriptor
        {
            Uri = Uri,
            Name = "Process catalog",
            Description = "Catalog of processes advertised by this server.",
            MimeType = McpResourceHelpers.JsonMimeType
        }
    };

    public IReadOnlyList<McpResourceTemplateDescriptor> DescribeTemplates() => [];

    public bool CanHandle(string uri) => string.Equals(uri, Uri, StringComparison.Ordinal);

    public Task<McpResourcesReadResult> ReadAsync(
        HttpContext httpContext,
        string uri,
        CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("GetProcessCatalog");
        var principal = McpAuthorizationHelper.EnsurePrincipal(httpContext);
        _jobService.EnsureCallerAuthorized(principal, OperatorResourceType.Catalog, OperatorOperation.Discover);
        McpLog.ResourceRead(_logger, Family, uri);

        var processes = _processCatalog
            .ListProcesses()
            .OrderBy(process => process.ProcessId, StringComparer.Ordinal)
            .Select(ToEntry)
            .ToList();

        var resource = new McpProcessCatalogResource
        {
            CatalogVersion = ResolveCatalogVersion(_processCatalog),
            Status = processes.Count == 0 ? "degraded" : "ok",
            Processes = processes,
            NotImplementedReason = processes.Count == 0 ? EmptyCatalogReason : null
        };

        var result = McpResourceHelpers.SingleJsonContent(uri, resource, McpJsonContext.Default.McpProcessCatalogResource);
        return Task.FromResult(result);
    }

    private static string ResolveCatalogVersion(IProcessCatalog catalog) =>
        catalog is BuiltInProcessCatalog
            ? BuiltInProcessCatalog.CatalogVersion
            : "honua.process_catalog.custom.v1";

    private static McpProcessEntry ToEntry(ProcessDefinition process) => new()
    {
        ProcessId = process.ProcessId,
        Name = process.Title,
        DisplayName = process.Title,
        Family = process.Category,
        Description = process.Description,
        Parameters = process.Parameters
            .Select(parameter => new McpProcessParameter
            {
                Name = parameter.Name,
                DisplayName = parameter.DisplayName,
                Description = parameter.Description,
                ValueType = ToWireValueType(parameter.ValueType),
                Required = parameter.Required,
                DefaultValue = parameter.DefaultValue
            })
            .ToList()
    };

    private static string ToWireValueType(ProcessParameterValueType valueType) => valueType switch
    {
        ProcessParameterValueType.Text => "text",
        ProcessParameterValueType.WholeNumber => "whole_number",
        ProcessParameterValueType.FloatingPoint => "floating_point",
        ProcessParameterValueType.Flag => "flag",
        ProcessParameterValueType.Wkb => "wkb",
        ProcessParameterValueType.WkbArray => "wkb_array",
        ProcessParameterValueType.Srid => "srid",
        ProcessParameterValueType.LayerId => "layer_id",
        _ => valueType.ToString()
    };
}
