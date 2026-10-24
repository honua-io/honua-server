// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Security.Abstractions;
using Honua.Geoprocessing;
using Honua.Infrastructure.Authentication;
using Honua.Ai.Protocols.Mcp.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Ai.Protocols.Mcp.Tools;

/// <summary>
/// Imports a completed built-in job artifact by canonical reference.
/// Inline prompt bytes, browser selections, local-run receipts, URIs, and
/// filesystem paths are not sources. <c>honua_publish_result</c> still refuses
/// an artifact that has no materialized table.
/// </summary>
internal sealed class IngestJobArtifactTool(ILogger<IngestJobArtifactTool> logger) : IMcpTool
{
    public const string ToolName = "honua_ingest_job_artifact";

    public string Name => ToolName;

    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Lifecycle;

    public McpToolDescriptor Describe() => new()
    {
        Name = ToolName,
        Title = "Ingest job artifact",
        Description =
            "Import a completed job artifact by reference (kind server-job-artifact, plus targetId, jobId, resultPackageId, artifactId, referenceSha256). "
            + "The server reads the stored artifact. Bytes, URLs, paths, browser-file, and local-process-artifact are refused. "
            + "honua_ingest_dataset is inline data; honua_publish_result still needs a materialized table. "
            + "targetSrid is the stored CRS. Set sourceSrid only when the producer CRS is unknown. Reuse intentId to recover one import.",
        InputSchema = McpToolSchemas.IngestJobArtifactArgumentSchema,
        OutputSchema = McpToolOutputSchemas.IngestJobArtifactOutputSchema,
        Annotations = McpToolAnnotationSets.Write("Ingest job artifact", destructive: false, idempotent: true),
    };

    public async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("IngestJobArtifact");
        McpLog.ToolInvoked(logger, ToolName, WorkflowFamily);

        var principal = McpAuthorizationHelper.EnsurePrincipal(httpContext);
        if (!await ImportAdminAuthorization.IsAuthorizedAsync(httpContext, principal, cancellationToken).ConfigureAwait(false))
        {
            throw new GeoprocessingAuthorizationException(
                requiresAuthentication: false,
                message: "Caller requires admin write authority to import a dataset.");
        }

        var argument = McpToolHelpers.ParseArguments(arguments, McpJsonContext.Default.McpIngestJobArtifactArgument);
        try
        {
            DatasetImportSourceRules.RequireServerJobArtifact(argument.Kind);
        }
        catch (ArgumentException exception)
        {
            throw new GeoprocessingValidationException(exception.Message);
        }

        var imports = httpContext.RequestServices.GetService<IFileImportService>();
        var jobService = httpContext.RequestServices.GetService<IGeoprocessingJobService>();
        var ledger = httpContext.RequestServices.GetService<IServerJobArtifactImportLedger>();
        if (imports is null || jobService is null || ledger is null)
        {
            return McpToolHelpers.SuccessResult(
                new McpIngestJobArtifactOutput
                {
                    Success = false,
                    IntentId = argument.IntentId ?? string.Empty,
                    ErrorMessage = "Job artifact import is unavailable in this composition.",
                },
                McpJsonContext.Default.McpIngestJobArtifactOutput);
        }

        var tenant = httpContext.RequestServices.GetService<ITenantContext>();
        if (tenant is null || !tenant.RequireTenantId(out var tenantId, out _))
        {
            throw new GeoprocessingValidationException("A tenant is required to import a job artifact.");
        }

        var receipt = await ServerJobArtifactImporter.ImportAsync(
            new ServerJobArtifactImportCommand(
                argument.IntentId ?? string.Empty,
                tenantId,
                McpAuthorizationHelper.ResolveActorId(principal),
                argument.Schema ?? string.Empty,
                argument.Table ?? string.Empty,
                new DatasetImportServerJobArtifactReference(
                    argument.TargetId ?? string.Empty,
                    argument.JobId ?? string.Empty,
                    argument.ResultPackageId ?? string.Empty,
                    argument.ArtifactId ?? string.Empty,
                    argument.ReferenceSha256 ?? string.Empty),
                argument.TargetSrid ?? 0,
                argument.SourceSrid),
            principal,
            jobService,
            imports,
            ledger,
            httpContext.RequestServices,
            cancellationToken).ConfigureAwait(false);

        var connection = await TryResolveCatalogConnectionIdAsync(httpContext.RequestServices, cancellationToken).ConfigureAwait(false);
        var warnings = new List<string>();
        if (connection.ConnectionId is null)
        {
            warnings.Add(connection.Ambiguous
                ? "More than one registered secure connection matches the catalog database, so connectionId is null."
                : "No unique registered secure connection matches the catalog database, so connectionId is null.");
        }

        return McpToolHelpers.SuccessResult(
            new McpIngestJobArtifactOutput
            {
                Success = true,
                IntentId = receipt.IntentId,
                DatasetName = argument.Table ?? receipt.Table,
                RowCount = receipt.FeatureCount,
                ConnectionId = connection.ConnectionId,
                Schema = receipt.Schema,
                Table = receipt.Table,
                Srid = receipt.TargetSrid,
                SourceSrid = receipt.SourceSrid,
                CrsState = receipt.CrsState,
                ContentSha256 = receipt.ContentSha256,
                GeometryColumn = IngestDatasetTool.ImportedGeometryColumn,
                PrimaryKey = IngestDatasetTool.ImportedPrimaryKey,
                Warnings = warnings,
            },
            McpJsonContext.Default.McpIngestJobArtifactOutput);
    }

    private static async Task<CatalogConnectionMatch> TryResolveCatalogConnectionIdAsync(
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        try
        {
            var resolver = services.GetService<ISecureConnectionResolver>();
            var registry = services.GetService<ISecureConnectionRegistry>();
            var matcher = services.GetService<ICatalogConnectionIdentityMatcher>();
            var configuration = services.GetService<IConfiguration>();
            var defaultConnectionString = configuration?["ConnectionStrings:DefaultConnection"];
            if (resolver is null || registry is null || matcher is null || string.IsNullOrWhiteSpace(defaultConnectionString))
            {
                return default;
            }

            string? matchedName = null;
            var connections = await registry.GetActiveConnectionsAsync(cancellationToken).ConfigureAwait(false);
            foreach (var connection in connections)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!connection.IsActive || string.IsNullOrWhiteSpace(connection.Name))
                {
                    continue;
                }

                try
                {
                    var connectionString = await resolver.ResolveConnectionStringAsync(connection.Name, cancellationToken).ConfigureAwait(false);
                    if (matcher.IsMatch(connection.NormalizedProvider, defaultConnectionString, connectionString))
                    {
                        if (matchedName is not null)
                        {
                            return new CatalogConnectionMatch(null, true);
                        }

                        matchedName = connection.Name;
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // One unresolvable connection must not hide a later match.
                }
            }

            return new CatalogConnectionMatch(matchedName, false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The connection hint is not the import receipt.
            return default;
        }
    }

    private readonly record struct CatalogConnectionMatch(string? ConnectionId, bool Ambiguous);
}
