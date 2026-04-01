// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.Import;
using Honua.Server.Features.Infrastructure.Monitoring;
using Honua.Server.Features.Infrastructure.Styling;

namespace Honua.Server.Features.Admin;

/// <summary>
/// GeoServices-focused migration evidence generator for parity and cutover-readiness reports.
/// </summary>
internal sealed partial class MigrationEvidenceGenerator(
    IGeoservicesImportService geoservicesImportService,
    IHttpClientFactory httpClientFactory,
    ILayerCatalog layerCatalog,
    ILayerStyleService layerStyleService,
    IDeployPreflightProbe deployPreflightProbe,
    ICoordinateTransformService coordinateTransformService,
    ILogger<MigrationEvidenceGenerator> logger) : IMigrationEvidenceGenerator
{
    private const string SchemaVersion = "migration-evidence/v1";
    private const string TargetHttpClientName = "migration-evidence";
    private const string ScopeDelimiter = "->";
    private const double ExtentTolerance = 0.001d;

    private readonly IGeoservicesImportService _geoservicesImportService = geoservicesImportService ?? throw new ArgumentNullException(nameof(geoservicesImportService));
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
    private readonly ILayerCatalog _layerCatalog = layerCatalog ?? throw new ArgumentNullException(nameof(layerCatalog));
    private readonly ILayerStyleService _layerStyleService = layerStyleService ?? throw new ArgumentNullException(nameof(layerStyleService));
    private readonly IDeployPreflightProbe _deployPreflightProbe = deployPreflightProbe ?? throw new ArgumentNullException(nameof(deployPreflightProbe));
    private readonly ICoordinateTransformService _coordinateTransformService = coordinateTransformService ?? throw new ArgumentNullException(nameof(coordinateTransformService));
    private readonly ILogger<MigrationEvidenceGenerator> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task<MigrationEvidenceReport> GenerateAsync(
        MigrationEvidenceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Provider != MigrationEvidenceProvider.ArcGisGeoservices)
        {
            throw new NotSupportedException($"Migration evidence provider '{request.Provider}' is not supported.");
        }

        Log.GenerationStarted(_logger, request.SourceServiceUrl, request.TargetServiceName, request.CutoverProfile);

        using var sourceClient = _httpClientFactory.CreateClient("import-source");
        using var targetClient = _httpClientFactory.CreateClient(TargetHttpClientName);
        ConfigureProbeClient(sourceClient, request.ProbeTimeoutSeconds);
        ConfigureProbeClient(targetClient, request.ProbeTimeoutSeconds);

        var sourceService = await _geoservicesImportService.DiscoverServiceAsync(
                new GeoservicesDiscoveryRequest
                {
                    ServiceUrl = request.SourceServiceUrl,
                    TimeoutSeconds = request.ProbeTimeoutSeconds
                },
                cancellationToken)
            .ConfigureAwait(false);

        var targetServiceResult = await GetJsonAsync(
                targetClient,
                BuildTargetServiceMetadataUrl(request.TargetBaseUrl, request.TargetServiceName),
                includeProbeNonce: true,
                cancellationToken)
            .ConfigureAwait(false);
        if (!targetServiceResult.IsSuccess || targetServiceResult.Body is null)
        {
            throw new InvalidOperationException(
                $"Unable to retrieve target service metadata for '{request.TargetServiceName}': {targetServiceResult.ErrorMessage ?? "unknown error"}");
        }

        var targetServices = await _layerCatalog.ListServicesAsync(cancellationToken).ConfigureAwait(false);
        var targetServiceDefinition = targetServices.FirstOrDefault(service =>
            string.Equals(service.Name, request.TargetServiceName, StringComparison.OrdinalIgnoreCase));

        var sourceLayerRecords = new List<LayerWorkItem>(request.Layers.Length);
        var targetLayerRecords = new List<LayerWorkItem>(request.Layers.Length);
        var capabilityChecks = new List<MigrationComparisonCheck>();
        var styleChecks = new List<MigrationComparisonCheck>();
        var dataChecks = new List<MigrationComparisonCheck>();
        var operationalChecks = new List<MigrationComparisonCheck>();
        var geodesyStatuses = new List<MigrationEvidenceStatus>(request.Layers.Length);

        capabilityChecks.Add(BuildServiceCapabilitiesCheck(sourceService, targetServiceResult.Body.Value));
        capabilityChecks.Add(BuildServiceQueryFormatsCheck(sourceService, targetServiceResult.Body.Value));

        foreach (var mapping in request.Layers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var sourceLayerInfo = sourceService.Layers.FirstOrDefault(layer => layer.Id == mapping.SourceLayerId);
            var sourceMetadataResult = sourceLayerInfo is null
                ? RemoteJsonResult.CreateFailure(HttpStatusCode.NotFound, "Source layer not found.", null, null)
                : await GetJsonAsync(
                        sourceClient,
                        BuildSourceLayerMetadataUrl(request.SourceServiceUrl, mapping.SourceLayerId),
                        includeProbeNonce: false,
                        cancellationToken)
                    .ConfigureAwait(false);

            var sourceSnapshot = BuildSourceLayerSnapshot(sourceLayerInfo, sourceMetadataResult, mapping.SourceLayerId);
            sourceLayerRecords.Add(new LayerWorkItem(mapping, sourceSnapshot, sourceMetadataResult));

            var targetMetadataResult = await GetJsonAsync(
                    targetClient,
                    BuildTargetLayerMetadataUrl(request.TargetBaseUrl, request.TargetServiceName, mapping.TargetLayerId),
                    includeProbeNonce: true,
                    cancellationToken)
                .ConfigureAwait(false);

            var targetLayerDefinition = targetServiceDefinition?.Layers.FirstOrDefault(layer => layer.Id == mapping.TargetLayerId);
            LayerStyleSnapshot? targetStyle = null;
            if (targetLayerDefinition != null)
            {
                targetStyle = await _layerStyleService.GetStyleAsync(targetLayerDefinition, cancellationToken).ConfigureAwait(false);
            }

            var targetSnapshot = BuildTargetLayerSnapshot(
                targetLayerDefinition,
                targetMetadataResult,
                targetStyle,
                mapping.TargetLayerId);
            targetLayerRecords.Add(new LayerWorkItem(mapping, targetSnapshot, targetMetadataResult));

            capabilityChecks.AddRange(BuildLayerCapabilityChecks(mapping, sourceSnapshot, targetSnapshot));
            styleChecks.Add(BuildStyleParityCheck(mapping, request, sourceMetadataResult.Body, targetStyle, targetSnapshot));

            var layerDataChecks = await BuildDataChecksAsync(
                    request,
                    sourceClient,
                    targetClient,
                    sourceMetadataResult,
                    targetMetadataResult,
                    mapping,
                    sourceSnapshot,
                    targetSnapshot,
                    cancellationToken)
                .ConfigureAwait(false);

            dataChecks.AddRange(layerDataChecks.Checks);
            if (layerDataChecks.GeodesyStatus.HasValue)
            {
                geodesyStatuses.Add(layerDataChecks.GeodesyStatus.Value);
            }
        }

        var preflight = await TryProbePreflightAsync(cancellationToken).ConfigureAwait(false);
        operationalChecks.AddRange(BuildOperationalChecks(request, preflight));

        var comparison = new MigrationEvidenceComparison
        {
            Capability = capabilityChecks.ToArray(),
            Style = styleChecks.ToArray(),
            Data = dataChecks.ToArray(),
            OperationalReadiness = operationalChecks.ToArray()
        };

        var readiness = BuildReadinessSummary(request, comparison, sourceLayerRecords, targetLayerRecords, preflight, geodesyStatuses);

        var report = new MigrationEvidenceReport
        {
            ReportId = Guid.NewGuid(),
            SchemaVersion = SchemaVersion,
            GeneratedAt = DateTimeOffset.UtcNow,
            ReportHash = string.Empty,
            Request = request,
            SourceBaseline = new MigrationEvidenceSourceBaseline
            {
                ServiceUrl = request.SourceServiceUrl,
                ServiceName = sourceService.ServiceName,
                Version = sourceService.Version,
                Capabilities = NormalizeArray(sourceService.Capabilities),
                SupportedQueryFormats = NormalizeArray(sourceService.SupportedQueryFormats),
                ServiceDigest = ComputeSha256Hex(JsonSerializer.Serialize(
                    sourceService,
                    GeoservicesImportApiJsonContext.Default.GeoservicesServiceInfo)),
                Layers = sourceLayerRecords.Select(static item => item.Snapshot).ToArray()
            },
            TargetSnapshot = new MigrationEvidenceTargetSnapshot
            {
                BaseUrl = request.TargetBaseUrl,
                ServiceName = request.TargetServiceName,
                ServiceDigest = ComputeCanonicalJsonHash(targetServiceResult.Body.Value),
                Capabilities = ParseDelimitedOrArray(targetServiceResult.Body.Value, "capabilities"),
                SupportedQueryFormats = ParseDelimitedOrArray(targetServiceResult.Body.Value, "supportedQueryFormats"),
                Layers = targetLayerRecords.Select(static item => item.Snapshot).ToArray(),
                OperationalSnapshot = preflight.Snapshot
            },
            Comparison = comparison,
            CutoverReadiness = readiness
        };

        var reportHash = ComputeReportHash(report);
        Log.GenerationCompleted(_logger, report.ReportId, readiness.State, reportHash);
        return report with { ReportHash = reportHash };
    }

    private static void ConfigureProbeClient(HttpClient client, int timeoutSeconds)
    {
        client.Timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 60));
        client.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue
        {
            NoCache = true,
            NoStore = true
        };

        if (!client.DefaultRequestHeaders.Pragma.Contains(new NameValueHeaderValue("no-cache")))
        {
            client.DefaultRequestHeaders.Pragma.Add(new NameValueHeaderValue("no-cache"));
        }
    }

    private async Task<DataCheckResult> BuildDataChecksAsync(
        MigrationEvidenceRequest request,
        HttpClient sourceClient,
        HttpClient targetClient,
        RemoteJsonResult sourceMetadataResult,
        RemoteJsonResult targetMetadataResult,
        MigrationEvidenceLayerMapping mapping,
        MigrationEvidenceLayerSnapshot sourceSnapshot,
        MigrationEvidenceLayerSnapshot targetSnapshot,
        CancellationToken cancellationToken)
    {
        var checks = new List<MigrationComparisonCheck>();

        if (!sourceMetadataResult.IsSuccess || sourceMetadataResult.Body is null)
        {
            checks.Add(CreateFailedProbeCheck(
                "core_query_parity",
                mapping,
                $"Source layer {mapping.SourceLayerId} metadata could not be resolved.",
                sourceMetadataResult.ErrorMessage));
            return new DataCheckResult(checks, MigrationEvidenceStatus.Fail);
        }

        if (!targetMetadataResult.IsSuccess || targetMetadataResult.Body is null)
        {
            checks.Add(CreateFailedProbeCheck(
                "core_query_parity",
                mapping,
                $"Target layer {mapping.TargetLayerId} metadata could not be resolved.",
                targetMetadataResult.ErrorMessage));
            return new DataCheckResult(checks, MigrationEvidenceStatus.Fail);
        }

        var fieldMappings = BuildFieldMappings(sourceMetadataResult.Body.Value, targetMetadataResult.Body.Value);
        var queryPageSize = Math.Clamp(request.QueryPageSize, 1, 100);
        var sampleRowCount = Math.Clamp(request.SampleRowCount, 1, 100);

        checks.Add(await BuildCoreQueryParityCheckAsync(
                sourceClient,
                targetClient,
                request,
                mapping,
                sourceMetadataResult.Body.Value,
                targetMetadataResult.Body.Value,
                fieldMappings,
                sampleRowCount,
                cancellationToken)
            .ConfigureAwait(false));

        checks.Add(await BuildReturnIdsOnlyCheckAsync(
                sourceClient,
                targetClient,
                request,
                mapping,
                cancellationToken)
            .ConfigureAwait(false));

        checks.Add(await BuildDistinctParityCheckAsync(
                sourceClient,
                targetClient,
                request,
                mapping,
                fieldMappings,
                cancellationToken)
            .ConfigureAwait(false));

        checks.Add(await BuildStatisticsParityCheckAsync(
                sourceClient,
                targetClient,
                request,
                mapping,
                fieldMappings,
                cancellationToken)
            .ConfigureAwait(false));

        checks.Add(await BuildGroupedStatisticsParityCheckAsync(
                sourceClient,
                targetClient,
                request,
                mapping,
                fieldMappings,
                cancellationToken)
            .ConfigureAwait(false));

        var extentCheck = await BuildSpatialEnvelopeParityCheckAsync(
                sourceClient,
                targetClient,
                request,
                mapping,
                sourceSnapshot,
                targetSnapshot,
                cancellationToken)
            .ConfigureAwait(false);
        checks.Add(extentCheck.Check);

        checks.Add(await BuildTimeQueryParityCheckAsync(
                sourceClient,
                targetClient,
                request,
                mapping,
                fieldMappings,
                cancellationToken)
            .ConfigureAwait(false));

        checks.Add(await BuildErrorShapeParityCheckAsync(
                sourceClient,
                targetClient,
                request,
                mapping,
                cancellationToken)
            .ConfigureAwait(false));

        checks.Add(await BuildGeoJsonParityCheckAsync(
                sourceClient,
                targetClient,
                request,
                mapping,
                sourceMetadataResult.Body.Value,
                targetMetadataResult.Body.Value,
                fieldMappings,
                sampleRowCount,
                cancellationToken)
            .ConfigureAwait(false));

        checks.Add(await BuildTransferLimitCheckAsync(
                sourceClient,
                targetClient,
                request,
                mapping,
                queryPageSize,
                cancellationToken)
            .ConfigureAwait(false));

        checks.Add(await BuildResultTypeParityCheckAsync(
                sourceClient,
                targetClient,
                request,
                mapping,
                sourceMetadataResult.Body.Value,
                targetMetadataResult.Body.Value,
                cancellationToken)
            .ConfigureAwait(false));

        return new DataCheckResult(checks, extentCheck.GeodesyStatus);
    }

    private static async Task<MigrationComparisonCheck> BuildCoreQueryParityCheckAsync(
        HttpClient sourceClient,
        HttpClient targetClient,
        MigrationEvidenceRequest request,
        MigrationEvidenceLayerMapping mapping,
        JsonElement sourceMetadata,
        JsonElement targetMetadata,
        FieldMappingSet fieldMappings,
        int sampleRowCount,
        CancellationToken cancellationToken)
    {
        if (fieldMappings.Entries.Length == 0)
        {
            return new MigrationComparisonCheck
            {
                CheckName = "core_query_parity",
                Status = MigrationEvidenceStatus.Fail,
                Scope = FormatScope(mapping),
                Summary = "No common canonical fields could be aligned between source and target.",
                Observations =
                [
                    new MigrationComparisonObservation
                    {
                        Name = "common_field_count",
                        Expected = ">=1",
                        Actual = "0"
                    }
                ]
            };
        }

        var sourceRows = await QueryRowsPageAsync(
                sourceClient,
                BuildSourceLayerQueryUrl(request.SourceServiceUrl, mapping.SourceLayerId),
                ResolveObjectIdField(sourceMetadata),
                fieldMappings.Entries.Select(static entry => entry.SourceField).ToArray(),
                sampleRowCount,
                cancellationToken)
            .ConfigureAwait(false);

        var targetRows = await QueryRowsPageAsync(
                targetClient,
                BuildTargetLayerQueryUrl(request.TargetBaseUrl, request.TargetServiceName, mapping.TargetLayerId),
                ResolveObjectIdField(targetMetadata) ?? "objectid",
                fieldMappings.Entries.Select(static entry => entry.TargetField).ToArray(),
                sampleRowCount,
                cancellationToken,
                includeProbeNonce: true)
            .ConfigureAwait(false);

        if (!sourceRows.IsSuccess || !targetRows.IsSuccess)
        {
            return CreateFailedProbeCheck(
                "core_query_parity",
                mapping,
                "One or more row-page probes failed.",
                $"{sourceRows.ErrorMessage ?? "unknown source error"} | {targetRows.ErrorMessage ?? "unknown target error"}");
        }

        var sourceCanonical = CanonicalizeFeatureRows(sourceRows.Body!.Value, fieldMappings, FeatureRowFieldOrigin.Source, geoJson: false);
        var targetCanonical = CanonicalizeFeatureRows(targetRows.Body!.Value, fieldMappings, FeatureRowFieldOrigin.Target, geoJson: false);
        var matched = sourceCanonical.SequenceEqual(targetCanonical, StringComparer.Ordinal);

        return new MigrationComparisonCheck
        {
            CheckName = "core_query_parity",
            Status = matched ? MigrationEvidenceStatus.Pass : MigrationEvidenceStatus.Fail,
            Scope = FormatScope(mapping),
            Summary = matched
                ? "Deterministic sample-row parity matched across source and target."
                : "Deterministic sample-row parity diverged.",
            Observations =
            [
                new MigrationComparisonObservation
                {
                    Name = "sample_row_count",
                    Expected = sourceCanonical.Length.ToString(CultureInfo.InvariantCulture),
                    Actual = targetCanonical.Length.ToString(CultureInfo.InvariantCulture)
                },
                new MigrationComparisonObservation
                {
                    Name = "sample_digest",
                    Expected = ComputeSha256Hex(string.Join('\n', sourceCanonical)),
                    Actual = ComputeSha256Hex(string.Join('\n', targetCanonical))
                }
            ]
        };
    }

    private static async Task<MigrationComparisonCheck> BuildReturnIdsOnlyCheckAsync(
        HttpClient sourceClient,
        HttpClient targetClient,
        MigrationEvidenceRequest request,
        MigrationEvidenceLayerMapping mapping,
        CancellationToken cancellationToken)
    {
        var sourceIds = await QueryObjectIdsAsync(
                sourceClient,
                BuildSourceLayerQueryUrl(request.SourceServiceUrl, mapping.SourceLayerId),
                cancellationToken)
            .ConfigureAwait(false);
        var targetIds = await QueryObjectIdsAsync(
                targetClient,
                BuildTargetLayerQueryUrl(request.TargetBaseUrl, request.TargetServiceName, mapping.TargetLayerId),
                cancellationToken,
                includeProbeNonce: true)
            .ConfigureAwait(false);

        if (!sourceIds.IsSuccess || !targetIds.IsSuccess)
        {
            return CreateFailedProbeCheck(
                "return_ids_only_parity",
                mapping,
                "Object ID probes failed.",
                $"{sourceIds.ErrorMessage ?? "unknown source error"} | {targetIds.ErrorMessage ?? "unknown target error"}");
        }

        var sourceIdsSorted = sourceIds.ObjectIds.OrderBy(static id => id).ToArray();
        var targetIdsSorted = targetIds.ObjectIds.OrderBy(static id => id).ToArray();
        var sourceCount = CountDistinct(sourceIdsSorted);
        var targetCount = CountDistinct(targetIdsSorted);
        var matched = sourceCount == sourceIdsSorted.Length &&
                      targetCount == targetIdsSorted.Length &&
                      sourceIdsSorted.SequenceEqual(targetIdsSorted);

        return new MigrationComparisonCheck
        {
            CheckName = "return_ids_only_parity",
            Status = matched ? MigrationEvidenceStatus.Pass : MigrationEvidenceStatus.Fail,
            Scope = FormatScope(mapping),
            Summary = matched
                ? "returnIdsOnly cardinality and uniqueness matched."
                : "returnIdsOnly cardinality or uniqueness diverged.",
            Observations =
            [
                new MigrationComparisonObservation
                {
                    Name = "source_ids",
                    Expected = sourceIdsSorted.Length.ToString(CultureInfo.InvariantCulture),
                    Actual = targetIdsSorted.Length.ToString(CultureInfo.InvariantCulture)
                },
                new MigrationComparisonObservation
                {
                    Name = "distinct_ids",
                    Expected = sourceCount.ToString(CultureInfo.InvariantCulture),
                    Actual = targetCount.ToString(CultureInfo.InvariantCulture)
                }
            ]
        };
    }

    private static async Task<MigrationComparisonCheck> BuildDistinctParityCheckAsync(
        HttpClient sourceClient,
        HttpClient targetClient,
        MigrationEvidenceRequest request,
        MigrationEvidenceLayerMapping mapping,
        FieldMappingSet fieldMappings,
        CancellationToken cancellationToken)
    {
        var distinctField = fieldMappings.StringField;
        if (distinctField is null)
        {
            return CreateNotApplicableCheck("distinct_parity", mapping, "No common string field was available for a distinct-value probe.");
        }

        var sourceDistinct = await QueryDistinctValuesAsync(
                sourceClient,
                BuildSourceLayerQueryUrl(request.SourceServiceUrl, mapping.SourceLayerId),
                distinctField.SourceField,
                cancellationToken)
            .ConfigureAwait(false);
        var targetDistinct = await QueryDistinctValuesAsync(
                targetClient,
                BuildTargetLayerQueryUrl(request.TargetBaseUrl, request.TargetServiceName, mapping.TargetLayerId),
                distinctField.TargetField,
                cancellationToken,
                includeProbeNonce: true)
            .ConfigureAwait(false);

        if (!sourceDistinct.IsSuccess || !targetDistinct.IsSuccess)
        {
            return CreateFailedProbeCheck(
                "distinct_parity",
                mapping,
                "Distinct-value probes failed.",
                $"{sourceDistinct.ErrorMessage ?? "unknown source error"} | {targetDistinct.ErrorMessage ?? "unknown target error"}");
        }

        var sourceValues = sourceDistinct.Values.OrderBy(static value => value, StringComparer.Ordinal).ToArray();
        var targetValues = targetDistinct.Values.OrderBy(static value => value, StringComparer.Ordinal).ToArray();
        var matched = sourceValues.SequenceEqual(targetValues, StringComparer.Ordinal);

        return new MigrationComparisonCheck
        {
            CheckName = "distinct_parity",
            Status = matched ? MigrationEvidenceStatus.Pass : MigrationEvidenceStatus.Fail,
            Scope = FormatScope(mapping),
            Summary = matched
                ? $"Distinct values matched for field '{distinctField.CanonicalField}'."
                : $"Distinct values diverged for field '{distinctField.CanonicalField}'.",
            Observations =
            [
                new MigrationComparisonObservation
                {
                    Name = "distinct_count",
                    Expected = sourceValues.Length.ToString(CultureInfo.InvariantCulture),
                    Actual = targetValues.Length.ToString(CultureInfo.InvariantCulture)
                }
            ]
        };
    }

    private static async Task<MigrationComparisonCheck> BuildStatisticsParityCheckAsync(
        HttpClient sourceClient,
        HttpClient targetClient,
        MigrationEvidenceRequest request,
        MigrationEvidenceLayerMapping mapping,
        FieldMappingSet fieldMappings,
        CancellationToken cancellationToken)
    {
        var numericField = fieldMappings.NumericField;
        if (numericField is null)
        {
            return CreateNotApplicableCheck("statistics_parity", mapping, "No common numeric field was available for a statistics probe.");
        }

        var sourceStats = await QueryStatisticsAsync(
                sourceClient,
                BuildSourceLayerQueryUrl(request.SourceServiceUrl, mapping.SourceLayerId),
                numericField.SourceField,
                cancellationToken)
            .ConfigureAwait(false);
        var targetStats = await QueryStatisticsAsync(
                targetClient,
                BuildTargetLayerQueryUrl(request.TargetBaseUrl, request.TargetServiceName, mapping.TargetLayerId),
                numericField.TargetField,
                cancellationToken,
                includeProbeNonce: true)
            .ConfigureAwait(false);

        if (!sourceStats.IsSuccess || !targetStats.IsSuccess)
        {
            return CreateFailedProbeCheck(
                "statistics_parity",
                mapping,
                "Statistics probes failed.",
                $"{sourceStats.ErrorMessage ?? "unknown source error"} | {targetStats.ErrorMessage ?? "unknown target error"}");
        }

        var matched = sourceStats.Count == targetStats.Count &&
            NearlyEqual(sourceStats.Min, targetStats.Min) &&
            NearlyEqual(sourceStats.Max, targetStats.Max);

        return new MigrationComparisonCheck
        {
            CheckName = "statistics_parity",
            Status = matched ? MigrationEvidenceStatus.Pass : MigrationEvidenceStatus.Fail,
            Scope = FormatScope(mapping),
            Summary = matched
                ? $"Count/min/max statistics matched for field '{numericField.CanonicalField}'."
                : $"Count/min/max statistics diverged for field '{numericField.CanonicalField}'.",
            Observations =
            [
                new MigrationComparisonObservation { Name = "count", Expected = sourceStats.Count.ToString(CultureInfo.InvariantCulture), Actual = targetStats.Count.ToString(CultureInfo.InvariantCulture) },
                new MigrationComparisonObservation { Name = "min", Expected = FormatDouble(sourceStats.Min), Actual = FormatDouble(targetStats.Min) },
                new MigrationComparisonObservation { Name = "max", Expected = FormatDouble(sourceStats.Max), Actual = FormatDouble(targetStats.Max) }
            ]
        };
    }

    private static async Task<MigrationComparisonCheck> BuildGroupedStatisticsParityCheckAsync(
        HttpClient sourceClient,
        HttpClient targetClient,
        MigrationEvidenceRequest request,
        MigrationEvidenceLayerMapping mapping,
        FieldMappingSet fieldMappings,
        CancellationToken cancellationToken)
    {
        var groupField = fieldMappings.StringField;
        if (groupField is null)
        {
            return CreateNotApplicableCheck("grouped_statistics_parity", mapping, "No common string field was available for a grouped-statistics probe.");
        }

        var sourceGroups = await QueryGroupedCountsAsync(
                sourceClient,
                BuildSourceLayerQueryUrl(request.SourceServiceUrl, mapping.SourceLayerId),
                groupField.SourceField,
                cancellationToken)
            .ConfigureAwait(false);
        var targetGroups = await QueryGroupedCountsAsync(
                targetClient,
                BuildTargetLayerQueryUrl(request.TargetBaseUrl, request.TargetServiceName, mapping.TargetLayerId),
                groupField.TargetField,
                cancellationToken,
                includeProbeNonce: true)
            .ConfigureAwait(false);

        if (!sourceGroups.IsSuccess || !targetGroups.IsSuccess)
        {
            return CreateFailedProbeCheck(
                "grouped_statistics_parity",
                mapping,
                "Grouped statistics probes failed.",
                $"{sourceGroups.ErrorMessage ?? "unknown source error"} | {targetGroups.ErrorMessage ?? "unknown target error"}");
        }

        var matched = sourceGroups.Groups.SequenceEqual(targetGroups.Groups);
        return new MigrationComparisonCheck
        {
            CheckName = "grouped_statistics_parity",
            Status = matched ? MigrationEvidenceStatus.Pass : MigrationEvidenceStatus.Fail,
            Scope = FormatScope(mapping),
            Summary = matched
                ? $"Grouped count statistics matched for field '{groupField.CanonicalField}'."
                : $"Grouped count statistics diverged for field '{groupField.CanonicalField}'.",
            Observations =
            [
                new MigrationComparisonObservation
                {
                    Name = "group_digest",
                    Expected = ComputeSha256Hex(string.Join('\n', sourceGroups.Groups.Select(static item => $"{item.Key}:{item.Value}"))),
                    Actual = ComputeSha256Hex(string.Join('\n', targetGroups.Groups.Select(static item => $"{item.Key}:{item.Value}")))
                }
            ]
        };
    }

    private async Task<SpatialExtentCheckResult> BuildSpatialEnvelopeParityCheckAsync(
        HttpClient sourceClient,
        HttpClient targetClient,
        MigrationEvidenceRequest request,
        MigrationEvidenceLayerMapping mapping,
        MigrationEvidenceLayerSnapshot sourceSnapshot,
        MigrationEvidenceLayerSnapshot targetSnapshot,
        CancellationToken cancellationToken)
    {
        if (sourceSnapshot.Extent is null || targetSnapshot.Extent is null)
        {
            return new SpatialExtentCheckResult(
                CreateNotApplicableCheck("spatial_envelope_parity", mapping, "One or both layer extents were unavailable."),
                MigrationEvidenceStatus.NotApplicable);
        }

        var normalizedSource = await NormalizeExtentAsync(sourceSnapshot.Extent, cancellationToken).ConfigureAwait(false);
        var normalizedTarget = await NormalizeExtentAsync(targetSnapshot.Extent, cancellationToken).ConfigureAwait(false);
        if (normalizedSource is null || normalizedTarget is null)
        {
            return new SpatialExtentCheckResult(
                CreateFailedProbeCheck(
                    "spatial_envelope_parity",
                    mapping,
                    "Extent normalization to EPSG:4326 failed.",
                    "One or more source/target extents could not be transformed."),
                MigrationEvidenceStatus.Fail);
        }

        var matched =
            NearlyEqual(normalizedSource.Value.MinX, normalizedTarget.Value.MinX, ExtentTolerance) &&
            NearlyEqual(normalizedSource.Value.MinY, normalizedTarget.Value.MinY, ExtentTolerance) &&
            NearlyEqual(normalizedSource.Value.MaxX, normalizedTarget.Value.MaxX, ExtentTolerance) &&
            NearlyEqual(normalizedSource.Value.MaxY, normalizedTarget.Value.MaxY, ExtentTolerance);

        return new SpatialExtentCheckResult(
            new MigrationComparisonCheck
            {
                CheckName = "spatial_envelope_parity",
                Status = matched ? MigrationEvidenceStatus.Pass : MigrationEvidenceStatus.Fail,
                Scope = FormatScope(mapping),
                Summary = matched
                    ? "Normalized spatial extents matched within tolerance."
                    : "Normalized spatial extents diverged beyond tolerance.",
                Observations =
                [
                    new MigrationComparisonObservation { Name = "xmin", Expected = FormatDouble(normalizedSource.Value.MinX), Actual = FormatDouble(normalizedTarget.Value.MinX) },
                    new MigrationComparisonObservation { Name = "ymin", Expected = FormatDouble(normalizedSource.Value.MinY), Actual = FormatDouble(normalizedTarget.Value.MinY) },
                    new MigrationComparisonObservation { Name = "xmax", Expected = FormatDouble(normalizedSource.Value.MaxX), Actual = FormatDouble(normalizedTarget.Value.MaxX) },
                    new MigrationComparisonObservation { Name = "ymax", Expected = FormatDouble(normalizedSource.Value.MaxY), Actual = FormatDouble(normalizedTarget.Value.MaxY) }
                ]
            },
            matched ? MigrationEvidenceStatus.Pass : MigrationEvidenceStatus.Fail);
    }

    private static async Task<MigrationComparisonCheck> BuildTimeQueryParityCheckAsync(
        HttpClient sourceClient,
        HttpClient targetClient,
        MigrationEvidenceRequest request,
        MigrationEvidenceLayerMapping mapping,
        FieldMappingSet fieldMappings,
        CancellationToken cancellationToken)
    {
        var dateField = fieldMappings.DateField;
        if (dateField is null)
        {
            return CreateNotApplicableCheck("time_query_parity", mapping, "No common date field was available for a time-query probe.");
        }

        var sourceStats = await QueryDateRangeAsync(
                sourceClient,
                BuildSourceLayerQueryUrl(request.SourceServiceUrl, mapping.SourceLayerId),
                dateField.SourceField,
                cancellationToken)
            .ConfigureAwait(false);
        var targetStats = await QueryDateRangeAsync(
                targetClient,
                BuildTargetLayerQueryUrl(request.TargetBaseUrl, request.TargetServiceName, mapping.TargetLayerId),
                dateField.TargetField,
                cancellationToken,
                includeProbeNonce: true)
            .ConfigureAwait(false);

        if (!sourceStats.IsSuccess || !targetStats.IsSuccess || sourceStats.MinEpochMs is null || sourceStats.MaxEpochMs is null || targetStats.MinEpochMs is null || targetStats.MaxEpochMs is null)
        {
            return CreateNotApplicableCheck("time_query_parity", mapping, "Date range statistics were unavailable for one or both sides.");
        }

        var timeWindow = $"{sourceStats.MinEpochMs.Value},{sourceStats.MaxEpochMs.Value}";
        var sourceCount = await QueryCountAsync(
                sourceClient,
                BuildSourceLayerQueryUrl(request.SourceServiceUrl, mapping.SourceLayerId),
                cancellationToken,
                timeWindow)
            .ConfigureAwait(false);
        var targetCount = await QueryCountAsync(
                targetClient,
                BuildTargetLayerQueryUrl(request.TargetBaseUrl, request.TargetServiceName, mapping.TargetLayerId),
                cancellationToken,
                timeWindow,
                includeProbeNonce: true)
            .ConfigureAwait(false);

        if (!sourceCount.IsSuccess || !targetCount.IsSuccess)
        {
            return CreateFailedProbeCheck(
                "time_query_parity",
                mapping,
                "Time-window count probes failed.",
                $"{sourceCount.ErrorMessage ?? "unknown source error"} | {targetCount.ErrorMessage ?? "unknown target error"}");
        }

        var matched = sourceCount.Count == targetCount.Count;
        return new MigrationComparisonCheck
        {
            CheckName = "time_query_parity",
            Status = matched ? MigrationEvidenceStatus.Pass : MigrationEvidenceStatus.Fail,
            Scope = FormatScope(mapping),
            Summary = matched
                ? $"Time-window count matched for field '{dateField.CanonicalField}'."
                : $"Time-window count diverged for field '{dateField.CanonicalField}'.",
            Observations =
            [
                new MigrationComparisonObservation { Name = "time_window", Expected = timeWindow, Actual = timeWindow },
                new MigrationComparisonObservation { Name = "count", Expected = sourceCount.Count.ToString(CultureInfo.InvariantCulture), Actual = targetCount.Count.ToString(CultureInfo.InvariantCulture) }
            ]
        };
    }

    private static async Task<MigrationComparisonCheck> BuildErrorShapeParityCheckAsync(
        HttpClient sourceClient,
        HttpClient targetClient,
        MigrationEvidenceRequest request,
        MigrationEvidenceLayerMapping mapping,
        CancellationToken cancellationToken)
    {
        var source = await QueryInvalidTimeAsync(
                sourceClient,
                BuildSourceLayerQueryUrl(request.SourceServiceUrl, mapping.SourceLayerId),
                cancellationToken)
            .ConfigureAwait(false);
        var target = await QueryInvalidTimeAsync(
                targetClient,
                BuildTargetLayerQueryUrl(request.TargetBaseUrl, request.TargetServiceName, mapping.TargetLayerId),
                cancellationToken,
                includeProbeNonce: true)
            .ConfigureAwait(false);

        var matched = source.IsError && target.IsError &&
            GetStatusClass(source.StatusCode) == GetStatusClass(target.StatusCode) &&
            source.ErrorCode.HasValue == target.ErrorCode.HasValue;

        return new MigrationComparisonCheck
        {
            CheckName = "error_shape_parity",
            Status = matched ? MigrationEvidenceStatus.Pass : MigrationEvidenceStatus.Fail,
            Scope = FormatScope(mapping),
            Summary = matched
                ? "Invalid query error shape matched at the status-class and code-presence level."
                : "Invalid query error shape diverged.",
            Observations =
            [
                new MigrationComparisonObservation { Name = "source_status", Expected = GetStatusClass(source.StatusCode).ToString(CultureInfo.InvariantCulture), Actual = GetStatusClass(target.StatusCode).ToString(CultureInfo.InvariantCulture) },
                new MigrationComparisonObservation { Name = "source_error_code", Expected = source.ErrorCode?.ToString(CultureInfo.InvariantCulture), Actual = target.ErrorCode?.ToString(CultureInfo.InvariantCulture) }
            ]
        };
    }

    private static async Task<MigrationComparisonCheck> BuildGeoJsonParityCheckAsync(
        HttpClient sourceClient,
        HttpClient targetClient,
        MigrationEvidenceRequest request,
        MigrationEvidenceLayerMapping mapping,
        JsonElement sourceMetadata,
        JsonElement targetMetadata,
        FieldMappingSet fieldMappings,
        int sampleRowCount,
        CancellationToken cancellationToken)
    {
        var sourceSupports = SupportsQueryFormat(sourceMetadata, "geojson");
        var targetSupports = SupportsQueryFormat(targetMetadata, "geojson");
        if (!sourceSupports || !targetSupports)
        {
            return CreateNotApplicableCheck("geojson_query_parity", mapping, "Source or target did not advertise GeoJSON query support.");
        }

        var source = await QueryGeoJsonAsync(
                sourceClient,
                BuildSourceLayerQueryUrl(request.SourceServiceUrl, mapping.SourceLayerId),
                sampleRowCount,
                cancellationToken)
            .ConfigureAwait(false);
        var target = await QueryGeoJsonAsync(
                targetClient,
                BuildTargetLayerQueryUrl(request.TargetBaseUrl, request.TargetServiceName, mapping.TargetLayerId),
                sampleRowCount,
                cancellationToken,
                includeProbeNonce: true)
            .ConfigureAwait(false);

        if (!source.IsSuccess || !target.IsSuccess || source.Body is null || target.Body is null)
        {
            return CreateFailedProbeCheck(
                "geojson_query_parity",
                mapping,
                "GeoJSON probes failed.",
                $"{source.ErrorMessage ?? "unknown source error"} | {target.ErrorMessage ?? "unknown target error"}");
        }

        var sourceFeatures = CanonicalizeFeatureRows(source.Body.Value, fieldMappings, FeatureRowFieldOrigin.Source, geoJson: true);
        var targetFeatures = CanonicalizeFeatureRows(target.Body.Value, fieldMappings, FeatureRowFieldOrigin.Target, geoJson: true);
        var matched = sourceFeatures.SequenceEqual(targetFeatures, StringComparer.Ordinal);

        return new MigrationComparisonCheck
        {
            CheckName = "geojson_query_parity",
            Status = matched ? MigrationEvidenceStatus.Pass : MigrationEvidenceStatus.Fail,
            Scope = FormatScope(mapping),
            Summary = matched
                ? "GeoJSON sample-query parity matched."
                : "GeoJSON sample-query parity diverged.",
            Observations =
            [
                new MigrationComparisonObservation
                {
                    Name = "feature_digest",
                    Expected = ComputeSha256Hex(string.Join('\n', sourceFeatures)),
                    Actual = ComputeSha256Hex(string.Join('\n', targetFeatures))
                }
            ]
        };
    }

    private static async Task<MigrationComparisonCheck> BuildTransferLimitCheckAsync(
        HttpClient sourceClient,
        HttpClient targetClient,
        MigrationEvidenceRequest request,
        MigrationEvidenceLayerMapping mapping,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var source = await QueryRowsPageAsync(
                sourceClient,
                BuildSourceLayerQueryUrl(request.SourceServiceUrl, mapping.SourceLayerId),
                "objectid",
                ["*"],
                pageSize,
                cancellationToken)
            .ConfigureAwait(false);
        var target = await QueryRowsPageAsync(
                targetClient,
                BuildTargetLayerQueryUrl(request.TargetBaseUrl, request.TargetServiceName, mapping.TargetLayerId),
                "objectid",
                ["*"],
                pageSize,
                cancellationToken,
                includeProbeNonce: true)
            .ConfigureAwait(false);

        if (!source.IsSuccess || !target.IsSuccess)
        {
            return CreateFailedProbeCheck(
                "transfer_limit_flag_parity",
                mapping,
                "Transfer-limit probes failed.",
                $"{source.ErrorMessage ?? "unknown source error"} | {target.ErrorMessage ?? "unknown target error"}");
        }

        var matched = source.ExceededTransferLimit == target.ExceededTransferLimit;
        return new MigrationComparisonCheck
        {
            CheckName = "transfer_limit_flag_parity",
            Status = matched ? MigrationEvidenceStatus.Pass : MigrationEvidenceStatus.Fail,
            Scope = FormatScope(mapping),
            Summary = matched
                ? "Exceeded-transfer-limit flags matched."
                : "Exceeded-transfer-limit flags diverged.",
            Observations =
            [
                new MigrationComparisonObservation
                {
                    Name = "exceeded_transfer_limit",
                    Expected = source.ExceededTransferLimit.ToString(CultureInfo.InvariantCulture),
                    Actual = target.ExceededTransferLimit.ToString(CultureInfo.InvariantCulture)
                },
                new MigrationComparisonObservation
                {
                    Name = "page_size",
                    Expected = pageSize.ToString(CultureInfo.InvariantCulture),
                    Actual = pageSize.ToString(CultureInfo.InvariantCulture)
                }
            ]
        };
    }

    private static async Task<MigrationComparisonCheck> BuildResultTypeParityCheckAsync(
        HttpClient sourceClient,
        HttpClient targetClient,
        MigrationEvidenceRequest request,
        MigrationEvidenceLayerMapping mapping,
        JsonElement sourceMetadata,
        JsonElement targetMetadata,
        CancellationToken cancellationToken)
    {
        if (!SupportsResultTypeQuery(sourceMetadata) || !SupportsResultTypeQuery(targetMetadata))
        {
            return CreateNotApplicableCheck("result_type_window_parity", mapping, "Source or target did not advertise resultType query support.");
        }

        var sourceStandard = await QueryCountAsync(
                sourceClient,
                BuildSourceLayerQueryUrl(request.SourceServiceUrl, mapping.SourceLayerId),
                cancellationToken,
                resultType: "standard")
            .ConfigureAwait(false);
        var targetStandard = await QueryCountAsync(
                targetClient,
                BuildTargetLayerQueryUrl(request.TargetBaseUrl, request.TargetServiceName, mapping.TargetLayerId),
                cancellationToken,
                resultType: "standard",
                includeProbeNonce: true)
            .ConfigureAwait(false);
        var sourceTile = await QueryCountAsync(
                sourceClient,
                BuildSourceLayerQueryUrl(request.SourceServiceUrl, mapping.SourceLayerId),
                cancellationToken,
                resultType: "tile")
            .ConfigureAwait(false);
        var targetTile = await QueryCountAsync(
                targetClient,
                BuildTargetLayerQueryUrl(request.TargetBaseUrl, request.TargetServiceName, mapping.TargetLayerId),
                cancellationToken,
                resultType: "tile",
                includeProbeNonce: true)
            .ConfigureAwait(false);

        if (!sourceStandard.IsSuccess || !targetStandard.IsSuccess || !sourceTile.IsSuccess || !targetTile.IsSuccess)
        {
            return CreateFailedProbeCheck(
                "result_type_window_parity",
                mapping,
                "resultType probes failed.",
                $"{sourceStandard.ErrorMessage ?? "unknown source error"} | {targetStandard.ErrorMessage ?? "unknown target error"}");
        }

        var matched = sourceStandard.Count == targetStandard.Count && sourceTile.Count == targetTile.Count;
        return new MigrationComparisonCheck
        {
            CheckName = "result_type_window_parity",
            Status = matched ? MigrationEvidenceStatus.Pass : MigrationEvidenceStatus.Fail,
            Scope = FormatScope(mapping),
            Summary = matched
                ? "resultType=standard/tile count parity matched."
                : "resultType=standard/tile count parity diverged.",
            Observations =
            [
                new MigrationComparisonObservation { Name = "standard_count", Expected = sourceStandard.Count.ToString(CultureInfo.InvariantCulture), Actual = targetStandard.Count.ToString(CultureInfo.InvariantCulture) },
                new MigrationComparisonObservation { Name = "tile_count", Expected = sourceTile.Count.ToString(CultureInfo.InvariantCulture), Actual = targetTile.Count.ToString(CultureInfo.InvariantCulture) }
            ]
        };
    }

    private static MigrationComparisonCheck BuildServiceCapabilitiesCheck(GeoservicesServiceInfo sourceService, JsonElement targetServiceMetadata)
    {
        var sourceCapabilities = NormalizeArray(sourceService.Capabilities);
        var targetCapabilities = ParseDelimitedOrArray(targetServiceMetadata, "capabilities");
        var matched = sourceCapabilities.SequenceEqual(targetCapabilities, StringComparer.Ordinal);

        return new MigrationComparisonCheck
        {
            CheckName = "service_capabilities",
            Status = matched ? MigrationEvidenceStatus.Pass : MigrationEvidenceStatus.Fail,
            Scope = "service",
            Summary = matched
                ? "Service-level capabilities matched."
                : "Service-level capabilities diverged.",
            Observations =
            [
                new MigrationComparisonObservation
                {
                    Name = "capabilities",
                    Expected = string.Join(",", sourceCapabilities),
                    Actual = string.Join(",", targetCapabilities)
                }
            ]
        };
    }

    private static MigrationComparisonCheck BuildServiceQueryFormatsCheck(GeoservicesServiceInfo sourceService, JsonElement targetServiceMetadata)
    {
        var sourceFormats = NormalizeArray(sourceService.SupportedQueryFormats);
        var targetFormats = ParseDelimitedOrArray(targetServiceMetadata, "supportedQueryFormats");
        var matched = sourceFormats.SequenceEqual(targetFormats, StringComparer.Ordinal);

        return new MigrationComparisonCheck
        {
            CheckName = "service_query_formats",
            Status = matched ? MigrationEvidenceStatus.Pass : MigrationEvidenceStatus.Fail,
            Scope = "service",
            Summary = matched
                ? "Service-level query formats matched."
                : "Service-level query formats diverged.",
            Observations =
            [
                new MigrationComparisonObservation
                {
                    Name = "supported_query_formats",
                    Expected = string.Join(",", sourceFormats),
                    Actual = string.Join(",", targetFormats)
                }
            ]
        };
    }

    private static IEnumerable<MigrationComparisonCheck> BuildLayerCapabilityChecks(
        MigrationEvidenceLayerMapping mapping,
        MigrationEvidenceLayerSnapshot sourceSnapshot,
        MigrationEvidenceLayerSnapshot targetSnapshot)
    {
        var sourceFields = sourceSnapshot.Fields.Select(static field => field.CanonicalName).OrderBy(static value => value, StringComparer.Ordinal).ToArray();
        var targetFields = targetSnapshot.Fields.Select(static field => field.CanonicalName).OrderBy(static value => value, StringComparer.Ordinal).ToArray();
        var matchedFields = sourceFields.SequenceEqual(targetFields, StringComparer.Ordinal);
        var fieldNotes = sourceFields.Except(targetFields, StringComparer.Ordinal)
            .Select(static field => $"Missing target field: {field}")
            .Concat(targetFields.Except(sourceFields, StringComparer.Ordinal).Select(static field => $"Unexpected target field: {field}"))
            .ToArray();

        yield return new MigrationComparisonCheck
        {
            CheckName = "layer_exposure",
            Status = targetSnapshot.Notes.Any(static note => note.Contains("could not be resolved", StringComparison.OrdinalIgnoreCase))
                ? MigrationEvidenceStatus.Fail
                : MigrationEvidenceStatus.Pass,
            Scope = FormatScope(mapping),
            Summary = targetSnapshot.Notes.Any(static note => note.Contains("could not be resolved", StringComparison.OrdinalIgnoreCase))
                ? "Target layer was not publicly resolvable."
                : "Target layer was publicly resolvable.",
            Notes = targetSnapshot.Notes
        };

        yield return new MigrationComparisonCheck
        {
            CheckName = "field_schema_presence",
            Status = matchedFields ? MigrationEvidenceStatus.Pass : MigrationEvidenceStatus.Fail,
            Scope = FormatScope(mapping),
            Summary = matchedFields
                ? "Canonical field schema presence matched."
                : "Canonical field schema presence diverged.",
            Notes = fieldNotes
        };

        yield return new MigrationComparisonCheck
        {
            CheckName = "geometry_type_metadata",
            Status = string.Equals(sourceSnapshot.GeometryType, targetSnapshot.GeometryType, StringComparison.OrdinalIgnoreCase)
                ? MigrationEvidenceStatus.Pass
                : MigrationEvidenceStatus.Fail,
            Scope = FormatScope(mapping),
            Summary = string.Equals(sourceSnapshot.GeometryType, targetSnapshot.GeometryType, StringComparison.OrdinalIgnoreCase)
                ? "Layer geometry type matched."
                : "Layer geometry type diverged.",
            Observations =
            [
                new MigrationComparisonObservation
                {
                    Name = "geometry_type",
                    Expected = sourceSnapshot.GeometryType,
                    Actual = targetSnapshot.GeometryType
                }
            ]
        };

        yield return new MigrationComparisonCheck
        {
            CheckName = "attachment_support",
            Status = sourceSnapshot.HasAttachments == targetSnapshot.HasAttachments
                ? MigrationEvidenceStatus.Pass
                : MigrationEvidenceStatus.Fail,
            Scope = FormatScope(mapping),
            Summary = sourceSnapshot.HasAttachments == targetSnapshot.HasAttachments
                ? "Attachment support matched."
                : "Attachment support diverged.",
            Observations =
            [
                new MigrationComparisonObservation
                {
                    Name = "has_attachments",
                    Expected = sourceSnapshot.HasAttachments.ToString(CultureInfo.InvariantCulture),
                    Actual = targetSnapshot.HasAttachments.ToString(CultureInfo.InvariantCulture)
                }
            ]
        };
    }

    private static MigrationComparisonCheck BuildStyleParityCheck(
        MigrationEvidenceLayerMapping mapping,
        MigrationEvidenceRequest request,
        JsonElement? sourceMetadata,
        LayerStyleSnapshot? targetStyle,
        MigrationEvidenceLayerSnapshot targetSnapshot)
    {
        if (sourceMetadata is null || !TryGetPropertyCaseInsensitive(sourceMetadata.Value, "drawingInfo", out var drawingInfo))
        {
            var status = string.IsNullOrWhiteSpace(request.TranslationManifestRef)
                ? MigrationEvidenceStatus.NotApplicable
                : MigrationEvidenceStatus.Fail;
            return new MigrationComparisonCheck
            {
                CheckName = "style_parity",
                Status = status,
                Scope = FormatScope(mapping),
                Summary = status == MigrationEvidenceStatus.NotApplicable
                    ? "Source layer did not expose a canonical drawingInfo payload."
                    : "Style input was expected by provenance but no canonical drawingInfo payload was available.",
                Notes = status == MigrationEvidenceStatus.NotApplicable
                    ? []
                    : ["missing_input"]
            };
        }

        if (targetStyle?.DrawingInfo is null)
        {
            return new MigrationComparisonCheck
            {
                CheckName = "style_parity",
                Status = MigrationEvidenceStatus.Fail,
                Scope = FormatScope(mapping),
                Summary = "Target drawingInfo was unavailable for style comparison.",
                Notes = ["missing_target_style"]
            };
        }

        var sourceDigest = ComputeCanonicalJsonHash(drawingInfo);
        var targetDigest = ComputeCanonicalJsonHash(targetStyle.DrawingInfo.Value);
        var matched = string.Equals(sourceDigest, targetDigest, StringComparison.Ordinal);

        return new MigrationComparisonCheck
        {
            CheckName = "style_parity",
            Status = matched ? MigrationEvidenceStatus.Pass : MigrationEvidenceStatus.Warning,
            Scope = FormatScope(mapping),
            Summary = matched
                ? "Canonical drawingInfo parity matched."
                : "Canonical drawingInfo parity diverged.",
            Observations =
            [
                new MigrationComparisonObservation { Name = "source_style_digest", Expected = sourceDigest, Actual = targetDigest },
                new MigrationComparisonObservation { Name = "target_maplibre_digest", Expected = targetSnapshot.MapLibreStyleDigest, Actual = targetSnapshot.MapLibreStyleDigest }
            ]
        };
    }

    private static List<MigrationComparisonCheck> BuildOperationalChecks(
        MigrationEvidenceRequest request,
        PreflightProbeResult preflight)
    {
        var checks = new List<MigrationComparisonCheck>
        {
            new()
            {
                CheckName = "deploy_preflight_ready",
                Status = preflight.Snapshot.ReadyForCoordinatedDeploy ? MigrationEvidenceStatus.Pass : MigrationEvidenceStatus.Fail,
                Scope = "instance",
                Summary = preflight.Snapshot.ReadyForCoordinatedDeploy
                    ? "Deploy preflight reported the instance ready for coordinated deployment."
                    : "Deploy preflight reported the instance blocked.",
                Notes = string.IsNullOrWhiteSpace(preflight.Snapshot.Message) ? [] : [preflight.Snapshot.Message]
            },
            new()
            {
                CheckName = "migration_plan_clean",
                Status = preflight.Snapshot.MigrationPlanAvailable && !preflight.Snapshot.UpgradeRequired
                    ? MigrationEvidenceStatus.Pass
                    : MigrationEvidenceStatus.Fail,
                Scope = "instance",
                Summary = preflight.Snapshot.MigrationPlanAvailable && !preflight.Snapshot.UpgradeRequired
                    ? "Migration plan was available and clean."
                    : "Migration plan was unavailable or reported pending changes.",
                Notes = preflight.Snapshot.PendingScripts
            },
            new()
            {
                CheckName = "rollback_reference_present",
                Status = string.IsNullOrWhiteSpace(request.RollbackPlanReference)
                    ? MigrationEvidenceStatus.Fail
                    : MigrationEvidenceStatus.Pass,
                Scope = "request",
                Summary = string.IsNullOrWhiteSpace(request.RollbackPlanReference)
                    ? "Rollback plan reference was missing."
                    : "Rollback plan reference was supplied."
            }
        };

        if (preflight.Snapshot.DatabaseCompatible.HasValue)
        {
            checks.Add(new MigrationComparisonCheck
            {
                CheckName = "database_compatibility",
                Status = preflight.Snapshot.DatabaseCompatible.Value ? MigrationEvidenceStatus.Pass : MigrationEvidenceStatus.Fail,
                Scope = "instance",
                Summary = preflight.Snapshot.DatabaseCompatible.Value
                    ? "Database compatibility probe passed."
                    : "Database compatibility probe failed.",
                Notes = preflight.Snapshot.DatabaseCompatibilityWarnings
            });
        }

        return checks;
    }

    private static MigrationEvidenceReadinessSummary BuildReadinessSummary(
        MigrationEvidenceRequest request,
        MigrationEvidenceComparison comparison,
        IReadOnlyList<LayerWorkItem> sourceLayers,
        IReadOnlyList<LayerWorkItem> targetLayers,
        PreflightProbeResult preflight,
        List<MigrationEvidenceStatus> geodesyStatuses)
    {
        var sourceResolved = sourceLayers.All(static layer => layer.Result.IsSuccess);
        var targetResolved = targetLayers.All(static layer => layer.Result.IsSuccess);
        var capabilityStatus = AggregateStatus(comparison.Capability);
        var styleStatus = AggregateStatus(comparison.Style);
        var dataStatus = AggregateStatus(comparison.Data);
        var geodesyStatus = geodesyStatuses.Count == 0
            ? MigrationEvidenceStatus.NotApplicable
            : AggregateStatus(geodesyStatuses.Select(static status => new MigrationComparisonCheck
            {
                CheckName = "geodesy",
                Status = status,
                Scope = "layer",
                Summary = "geodesy"
            }));
        var translationInputsStatus = comparison.Style.Any(static check => check.Notes.Contains("missing_input"))
            ? MigrationEvidenceStatus.Fail
            : comparison.Style.All(static check => check.Status == MigrationEvidenceStatus.NotApplicable)
                ? MigrationEvidenceStatus.NotApplicable
                : MigrationEvidenceStatus.Pass;

        var checklist = new List<CutoverChecklistItem>
        {
            CreateChecklistItem("source_baseline_resolved", "pilot_required", sourceResolved ? MigrationEvidenceStatus.Pass : MigrationEvidenceStatus.Fail,
                sourceResolved ? "Source baseline and mapped layers were resolved." : "Source baseline and one or more mapped layers could not be resolved."),
            CreateChecklistItem("target_mapping_resolved", "pilot_required", targetResolved ? MigrationEvidenceStatus.Pass : MigrationEvidenceStatus.Fail,
                targetResolved ? "Target mappings were resolved." : "One or more target layers could not be resolved."),
            CreateChecklistItem("translation_inputs_resolved", "production_required", translationInputsStatus,
                translationInputsStatus switch
                {
                    MigrationEvidenceStatus.Pass => "Style inputs were available for style parity checks.",
                    MigrationEvidenceStatus.NotApplicable => "No canonical style input was required for this scope.",
                    _ => "Style inputs were missing for one or more layers."
                }),
            CreateChecklistItem("capability_parity", "pilot_required", capabilityStatus, FormatChecklistSummary("Capability parity", capabilityStatus)),
            CreateChecklistItem("style_parity", "production_required", styleStatus, FormatChecklistSummary("Style parity", styleStatus)),
            CreateChecklistItem("data_parity", "pilot_required", dataStatus, FormatChecklistSummary("Data parity", dataStatus)),
            CreateChecklistItem("geodesy_verified", "pilot_required", geodesyStatus, FormatChecklistSummary("Geodesy verification", geodesyStatus)),
            CreateChecklistItem("deploy_preflight_ready", "pilot_required",
                preflight.Snapshot.ReadyForCoordinatedDeploy ? MigrationEvidenceStatus.Pass : MigrationEvidenceStatus.Fail,
                preflight.Snapshot.ReadyForCoordinatedDeploy
                    ? "Deploy preflight reported the instance ready."
                    : "Deploy preflight reported the instance blocked."),
            CreateChecklistItem("migration_plan_clean", "pilot_required",
                preflight.Snapshot.MigrationPlanAvailable && !preflight.Snapshot.UpgradeRequired ? MigrationEvidenceStatus.Pass : MigrationEvidenceStatus.Fail,
                preflight.Snapshot.MigrationPlanAvailable && !preflight.Snapshot.UpgradeRequired
                    ? "Migration plan was available and clean."
                    : "Migration plan was unavailable or reported pending changes."),
            CreateChecklistItem("rollback_reference_present", "pilot_required",
                string.IsNullOrWhiteSpace(request.RollbackPlanReference) ? MigrationEvidenceStatus.Fail : MigrationEvidenceStatus.Pass,
                string.IsNullOrWhiteSpace(request.RollbackPlanReference)
                    ? "Rollback reference was missing."
                    : "Rollback reference was supplied.")
        };

        var blockingReasons = new List<string>();
        var warnings = new List<string>();

        foreach (var item in checklist)
        {
            var isPilotRequired = string.Equals(item.RequirementLevel, "pilot_required", StringComparison.Ordinal);
            var isProductionRequired = string.Equals(item.RequirementLevel, "production_required", StringComparison.Ordinal);

            if (item.Status == MigrationEvidenceStatus.Warning)
            {
                warnings.Add(item.Summary);
                if (request.CutoverProfile == MigrationCutoverProfile.Production && isProductionRequired)
                {
                    blockingReasons.Add(item.Summary);
                }
            }

            if (item.Status == MigrationEvidenceStatus.Fail &&
                (isPilotRequired || (request.CutoverProfile == MigrationCutoverProfile.Production && isProductionRequired)))
            {
                blockingReasons.Add(item.Summary);
            }
        }

        var readiness = blockingReasons.Count > 0
            ? MigrationReadinessState.Blocked
            : request.CutoverProfile == MigrationCutoverProfile.Production
                ? MigrationReadinessState.ProductionReady
                : MigrationReadinessState.PilotReady;

        return new MigrationEvidenceReadinessSummary
        {
            State = readiness,
            BlockingReasons = blockingReasons.Distinct(StringComparer.Ordinal).ToArray(),
            Warnings = warnings.Distinct(StringComparer.Ordinal).ToArray(),
            Checklist = checklist.ToArray()
        };
    }

    private static CutoverChecklistItem CreateChecklistItem(
        string name,
        string requirementLevel,
        MigrationEvidenceStatus status,
        string summary) =>
        new()
        {
            Name = name,
            RequirementLevel = requirementLevel,
            Status = status,
            Summary = summary
        };

    private static string FormatChecklistSummary(string label, MigrationEvidenceStatus status) =>
        status switch
        {
            MigrationEvidenceStatus.Pass => $"{label} passed.",
            MigrationEvidenceStatus.Warning => $"{label} completed with warning(s).",
            MigrationEvidenceStatus.NotApplicable => $"{label} was not applicable.",
            _ => $"{label} failed."
        };

    private async Task<PreflightProbeResult> TryProbePreflightAsync(CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await _deployPreflightProbe.ProbeAsync(cancellationToken).ConfigureAwait(false);
            return new PreflightProbeResult(new MigrationEvidenceOperationalSnapshot
            {
                Status = snapshot.Status,
                ReadyForCoordinatedDeploy = snapshot.ReadyForCoordinatedDeploy,
                Message = snapshot.Message,
                MigrationPlanAvailable = snapshot.Migration.PlanAvailable,
                UpgradeRequired = snapshot.Migration.UpgradeRequired,
                PendingScripts = snapshot.Migration.PendingScripts.ToArray(),
                ExecutedButNotDiscoveredScripts = snapshot.Migration.ExecutedButNotDiscoveredScripts.ToArray(),
                DatabaseCompatible = snapshot.DatabaseCompatibility?.IsCompatible,
                DatabaseCompatibilityWarnings = snapshot.DatabaseCompatibility?.Warnings.ToArray() ?? [],
                ErrorMessage = snapshot.DatabaseCompatibility?.ErrorMessage ?? snapshot.Migration.PlanError
            });
        }
        catch (Exception ex)
        {
            Log.PreflightProbeFailed(_logger, ex);
            return new PreflightProbeResult(new MigrationEvidenceOperationalSnapshot
            {
                Status = "blocked",
                ReadyForCoordinatedDeploy = false,
                Message = "Deploy preflight probe failed.",
                MigrationPlanAvailable = false,
                UpgradeRequired = false,
                ErrorMessage = ex.Message
            });
        }
    }

    private static MigrationEvidenceLayerSnapshot BuildSourceLayerSnapshot(
        GeoservicesLayerInfo? layerInfo,
        RemoteJsonResult metadataResult,
        int layerId)
    {
        var notes = new List<string>();
        if (!metadataResult.IsSuccess && !string.IsNullOrWhiteSpace(metadataResult.ErrorMessage))
        {
            notes.Add($"Layer metadata could not be resolved: {metadataResult.ErrorMessage}");
        }

        if (layerInfo == null)
        {
            notes.Add("Layer was not present in source discovery.");
            return new MigrationEvidenceLayerSnapshot
            {
                LayerId = layerId,
                Name = $"source:{layerId}",
                Fields = [],
                LayerDigest = ComputeSha256Hex(string.Empty),
                Notes = notes.ToArray()
            };
        }

        var metadata = metadataResult.Body;
        return new MigrationEvidenceLayerSnapshot
        {
            LayerId = layerInfo.Id,
            Name = layerInfo.Name,
            GeometryType = ReadOptionalString(metadata, "geometryType") ?? layerInfo.GeometryType,
            SpatialReferenceWkid = ReadSpatialReferenceWkid(metadata) ?? layerInfo.SpatialReferenceWkid,
            FeatureCount = ReadOptionalLong(metadata, "count") ?? layerInfo.FeatureCount,
            HasAttachments = ReadOptionalBool(metadata, "hasAttachments") ?? layerInfo.HasAttachments,
            Fields = ReadFieldSnapshots(metadata).Length > 0
                ? ReadFieldSnapshots(metadata)
                : layerInfo.Fields.Select(static field => new MigrationEvidenceFieldSnapshot
                {
                    Name = field.Name,
                    CanonicalName = field.Name.SanitizeFieldName(),
                    Type = field.Type,
                    Nullable = field.Nullable
                }).ToArray(),
            Extent = ReadExtent(metadata) ?? (layerInfo.Extent is null
                ? null
                : new MigrationEvidenceExtentSnapshot
                {
                    MinX = layerInfo.Extent.Xmin,
                    MinY = layerInfo.Extent.Ymin,
                    MaxX = layerInfo.Extent.Xmax,
                    MaxY = layerInfo.Extent.Ymax,
                    SpatialReferenceWkid = layerInfo.Extent.SpatialReferenceWkid
                }),
            LayerDigest = metadata is { } body
                ? ComputeCanonicalJsonHash(body)
                : ComputeSha256Hex(JsonSerializer.Serialize(layerInfo, GeoservicesImportApiJsonContext.Default.GeoservicesLayerInfo)),
            StyleDigest = metadata is { } layerMetadata && TryGetPropertyCaseInsensitive(layerMetadata, "drawingInfo", out var drawingInfo)
                ? ComputeCanonicalJsonHash(drawingInfo)
                : null,
            Notes = notes.ToArray()
        };
    }

    private static MigrationEvidenceLayerSnapshot BuildTargetLayerSnapshot(
        LayerDefinition? layerDefinition,
        RemoteJsonResult metadataResult,
        LayerStyleSnapshot? styleSnapshot,
        int layerId)
    {
        var notes = new List<string>();
        if (!metadataResult.IsSuccess && !string.IsNullOrWhiteSpace(metadataResult.ErrorMessage))
        {
            notes.Add($"Layer metadata could not be resolved: {metadataResult.ErrorMessage}");
        }

        if (layerDefinition == null)
        {
            notes.Add("Layer was not present in the target service catalog.");
        }

        var metadata = metadataResult.Body;
        var layerExtent = layerDefinition?.Extent;
        return new MigrationEvidenceLayerSnapshot
        {
            LayerId = layerDefinition?.Id ?? layerId,
            Name = layerDefinition?.Name ?? ReadOptionalString(metadata, "name") ?? $"target:{layerId}",
            GeometryType = ReadOptionalString(metadata, "geometryType") ?? layerDefinition?.GeometryType.ToString(),
            SpatialReferenceWkid = ReadSpatialReferenceWkid(metadata) ?? layerDefinition?.SpatialReference.Wkid,
            FeatureCount = ReadOptionalLong(metadata, "count"),
            HasAttachments = ReadOptionalBool(metadata, "hasAttachments") ?? layerDefinition?.SupportsAttachments ?? false,
            Fields = ReadFieldSnapshots(metadata).Length > 0
                ? ReadFieldSnapshots(metadata)
                : layerDefinition?.Fields.Select(static field => new MigrationEvidenceFieldSnapshot
                {
                    Name = field.Name,
                    CanonicalName = field.Name.SanitizeFieldName(),
                    Type = field.Type.ToString(),
                    Nullable = field.Nullable
                }).ToArray() ?? [],
            Extent = ReadExtent(metadata) ?? (layerExtent is null
                ? null
                : new MigrationEvidenceExtentSnapshot
                {
                    MinX = layerExtent.Value.MinX,
                    MinY = layerExtent.Value.MinY,
                    MaxX = layerExtent.Value.MaxX,
                    MaxY = layerExtent.Value.MaxY,
                    SpatialReferenceWkid = layerExtent.Value.SpatialReference
                }),
            LayerDigest = metadata is { } body
                ? ComputeCanonicalJsonHash(body)
                : ComputeSha256Hex($"{layerId}:{layerDefinition?.Name ?? string.Empty}"),
            StyleDigest = styleSnapshot?.DrawingInfo is { } drawingInfo
                ? ComputeCanonicalJsonHash(drawingInfo)
                : null,
            MapLibreStyleDigest = styleSnapshot?.MapLibreStyle is { } mapLibreStyle
                ? ComputeCanonicalJsonHash(mapLibreStyle)
                : null,
            Notes = notes.ToArray()
        };
    }

    private async Task<(double MinX, double MinY, double MaxX, double MaxY)?> NormalizeExtentAsync(
        MigrationEvidenceExtentSnapshot extent,
        CancellationToken cancellationToken)
    {
        if (extent.SpatialReferenceWkid is not { } wkid)
        {
            return null;
        }

        return await _coordinateTransformService.TransformExtentAsync(
                extent.MinX,
                extent.MinY,
                extent.MaxX,
                extent.MaxY,
                wkid,
                4326,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static FieldMappingSet BuildFieldMappings(JsonElement sourceMetadata, JsonElement targetMetadata)
    {
        var sourceFields = ReadFieldSnapshots(sourceMetadata);
        var targetFields = ReadFieldSnapshots(targetMetadata);
        var targetLookup = targetFields.ToDictionary(field => field.CanonicalName, StringComparer.OrdinalIgnoreCase);

        var entries = sourceFields
            .Where(source => targetLookup.ContainsKey(source.CanonicalName))
            .Select(source => new FieldMappingEntry(
                source.Name,
                targetLookup[source.CanonicalName].Name,
                source.CanonicalName,
                source.Type))
            .OrderBy(static entry => entry.CanonicalField, StringComparer.Ordinal)
            .ToArray();

        return new FieldMappingSet(
            entries,
            entries.FirstOrDefault(static entry => IsStringFieldType(entry.SourceType)),
            entries.FirstOrDefault(static entry => IsNumericFieldType(entry.SourceType)),
            entries.FirstOrDefault(static entry => IsDateFieldType(entry.SourceType)));
    }

    private static RemoteJsonResult CreateRemoteFailure(HttpStatusCode statusCode, string message)
        => RemoteJsonResult.CreateFailure(statusCode, message, null, null);

    internal static string[] CanonicalizeFeatureRows(
        JsonElement payload,
        FieldMappingSet fieldMappings,
        FeatureRowFieldOrigin fieldOrigin,
        bool geoJson)
    {
        if (!TryGetPropertyCaseInsensitive(payload, "features", out var features) || features.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var rows = new List<string>();
        foreach (var feature in features.EnumerateArray())
        {
            JsonElement attributes;
            if (geoJson)
            {
                if (!TryGetPropertyCaseInsensitive(feature, "properties", out attributes))
                {
                    continue;
                }
            }
            else
            {
                if (!TryGetPropertyCaseInsensitive(feature, "attributes", out attributes))
                {
                    continue;
                }
            }

            var parts = new List<string>(fieldMappings.Entries.Length);
            foreach (var entry in fieldMappings.Entries)
            {
                if (!TryGetMappedFieldValue(attributes, entry, fieldOrigin, out var value))
                {
                    parts.Add($"{entry.CanonicalField}=<missing>");
                    continue;
                }

                parts.Add($"{entry.CanonicalField}={NormalizeValue(value)}");
            }

            rows.Add(string.Join("|", parts));
        }

        return rows.ToArray();
    }

    private static bool TryGetMappedFieldValue(
        JsonElement attributes,
        FieldMappingEntry entry,
        FeatureRowFieldOrigin fieldOrigin,
        out JsonElement value)
    {
        var primaryField = fieldOrigin == FeatureRowFieldOrigin.Source ? entry.SourceField : entry.TargetField;
        var alternateField = fieldOrigin == FeatureRowFieldOrigin.Source ? entry.TargetField : entry.SourceField;

        return TryGetPropertyCaseInsensitive(attributes, primaryField, out value) ||
            TryGetDistinctProperty(attributes, entry.CanonicalField, primaryField, out value) ||
            TryGetDistinctProperty(attributes, alternateField, primaryField, entry.CanonicalField, out value);
    }

    private static bool TryGetDistinctProperty(
        JsonElement attributes,
        string candidate,
        string skippedProperty,
        out JsonElement value)
    {
        if (string.Equals(candidate, skippedProperty, StringComparison.OrdinalIgnoreCase))
        {
            value = default;
            return false;
        }

        return TryGetPropertyCaseInsensitive(attributes, candidate, out value);
    }

    private static bool TryGetDistinctProperty(
        JsonElement attributes,
        string candidate,
        string skippedPrimaryProperty,
        string skippedSecondaryProperty,
        out JsonElement value)
    {
        if (string.Equals(candidate, skippedPrimaryProperty, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(candidate, skippedSecondaryProperty, StringComparison.OrdinalIgnoreCase))
        {
            value = default;
            return false;
        }

        return TryGetPropertyCaseInsensitive(attributes, candidate, out value);
    }

    private static string BuildSourceLayerMetadataUrl(string serviceUrl, int layerId)
        => $"{serviceUrl.TrimEnd('/')}/{layerId}";

    private static string BuildSourceLayerQueryUrl(string serviceUrl, int layerId)
        => $"{serviceUrl.TrimEnd('/')}/{layerId}/query";

    private static string BuildTargetServiceMetadataUrl(string targetBaseUrl, string serviceName)
        => $"{targetBaseUrl.TrimEnd('/')}/rest/services/{Uri.EscapeDataString(serviceName)}/FeatureServer";

    private static string BuildTargetLayerMetadataUrl(string targetBaseUrl, string serviceName, int layerId)
        => $"{BuildTargetServiceMetadataUrl(targetBaseUrl, serviceName)}/{layerId}";

    private static string BuildTargetLayerQueryUrl(string targetBaseUrl, string serviceName, int layerId)
        => $"{BuildTargetLayerMetadataUrl(targetBaseUrl, serviceName, layerId)}/query";

    private static string AppendQueryString(string baseUrl, IReadOnlyDictionary<string, string?> parameters, bool includeProbeNonce)
    {
        var items = new List<string>(parameters.Count + 1);
        foreach (var pair in parameters)
        {
            if (pair.Value is null)
            {
                continue;
            }

            items.Add($"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}");
        }

        if (includeProbeNonce)
        {
            items.Add($"__honua_probe_nonce={Guid.NewGuid():N}");
        }

        return items.Count == 0
            ? baseUrl
            : $"{baseUrl}?{string.Join("&", items)}";
    }

    private static async Task<RemoteJsonResult> GetJsonAsync(
        HttpClient client,
        string baseUrl,
        bool includeProbeNonce,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string?>? parameters = null)
    {
        var finalUrl = AppendQueryString(
            baseUrl,
            parameters ?? new Dictionary<string, string?> { ["f"] = "json" },
            includeProbeNonce);

        using var request = new HttpRequestMessage(HttpMethod.Get, finalUrl);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var bodyText = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(bodyText))
        {
            return RemoteJsonResult.CreateFailure(response.StatusCode, "Empty response body.", null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(bodyText);
            var body = document.RootElement.Clone();
            if (TryGetPropertyCaseInsensitive(body, "error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                return RemoteJsonResult.CreateFailure(
                    response.StatusCode,
                    ReadOptionalString(error, "message") ?? "Remote service returned an error payload.",
                    ReadOptionalInt(error, "code"),
                    body);
            }

            return new RemoteJsonResult(true, response.StatusCode, null, null, body, ReadOptionalBool(body, "exceededTransferLimit") ?? false);
        }
        catch (JsonException)
        {
            return RemoteJsonResult.CreateFailure(response.StatusCode, "Response body was not valid JSON.", null, null);
        }
    }

    private static Task<RemoteJsonResult> QueryGeoJsonAsync(
        HttpClient client,
        string queryUrl,
        int sampleRowCount,
        CancellationToken cancellationToken,
        bool includeProbeNonce = false)
    {
        return GetJsonAsync(
            client,
            queryUrl,
            includeProbeNonce,
            cancellationToken,
            new Dictionary<string, string?>
            {
                ["where"] = "1=1",
                ["outFields"] = "*",
                ["returnGeometry"] = "false",
                ["resultRecordCount"] = sampleRowCount.ToString(CultureInfo.InvariantCulture),
                ["f"] = "geojson"
            });
    }

    private static Task<RemoteJsonResult> QueryRowsPageAsync(
        HttpClient client,
        string queryUrl,
        string? orderByField,
        string[] outFields,
        int pageSize,
        CancellationToken cancellationToken,
        bool includeProbeNonce = false)
    {
        return GetJsonAsync(
            client,
            queryUrl,
            includeProbeNonce,
            cancellationToken,
            new Dictionary<string, string?>
            {
                ["where"] = "1=1",
                ["outFields"] = outFields.Length == 0 ? "*" : string.Join(",", outFields),
                ["orderByFields"] = string.IsNullOrWhiteSpace(orderByField) ? null : orderByField,
                ["returnGeometry"] = "false",
                ["resultRecordCount"] = pageSize.ToString(CultureInfo.InvariantCulture),
                ["f"] = "json"
            });
    }

    private static Task<CountQueryResult> QueryCountAsync(
        HttpClient client,
        string queryUrl,
        CancellationToken cancellationToken,
        string? timeWindow = null,
        string? resultType = null,
        bool includeProbeNonce = false)
        => QueryCountAsync(client, queryUrl, "1=1", cancellationToken, timeWindow, resultType, includeProbeNonce);

    private static async Task<CountQueryResult> QueryCountAsync(
        HttpClient client,
        string queryUrl,
        string whereClause,
        CancellationToken cancellationToken,
        string? timeWindow = null,
        string? resultType = null,
        bool includeProbeNonce = false)
    {
        var result = await GetJsonAsync(
                client,
                queryUrl,
                includeProbeNonce,
                cancellationToken,
                new Dictionary<string, string?>
                {
                    ["where"] = whereClause,
                    ["time"] = timeWindow,
                    ["resultType"] = resultType,
                    ["returnCountOnly"] = "true",
                    ["f"] = "json"
                })
            .ConfigureAwait(false);

        if (!result.IsSuccess || result.Body is null)
        {
            return CountQueryResult.CreateFailure(result.ErrorMessage);
        }

        return TryGetPropertyCaseInsensitive(result.Body.Value, "count", out var countElement) && TryGetInt64(countElement, out var count)
            ? CountQueryResult.CreateSuccess(count)
            : CountQueryResult.CreateFailure("Count property was missing from response.");
    }

    private static async Task<ObjectIdQueryResult> QueryObjectIdsAsync(
        HttpClient client,
        string queryUrl,
        CancellationToken cancellationToken,
        bool includeProbeNonce = false)
    {
        var result = await GetJsonAsync(
                client,
                queryUrl,
                includeProbeNonce,
                cancellationToken,
                new Dictionary<string, string?>
                {
                    ["where"] = "1=1",
                    ["returnIdsOnly"] = "true",
                    ["f"] = "json"
                })
            .ConfigureAwait(false);

        if (!result.IsSuccess || result.Body is null)
        {
            return ObjectIdQueryResult.CreateFailure(result.ErrorMessage);
        }

        if (!TryGetPropertyCaseInsensitive(result.Body.Value, "objectIds", out var idsElement) || idsElement.ValueKind != JsonValueKind.Array)
        {
            return ObjectIdQueryResult.CreateFailure("objectIds were missing from the response.");
        }

        var ids = idsElement.EnumerateArray()
            .Select(value => TryGetInt64(value, out var parsed) ? parsed : (long?)null)
            .Where(static value => value.HasValue)
            .Select(static value => value!.Value)
            .ToArray();
        return ObjectIdQueryResult.CreateSuccess(ids);
    }

    private static async Task<DistinctQueryResult> QueryDistinctValuesAsync(
        HttpClient client,
        string queryUrl,
        string fieldName,
        CancellationToken cancellationToken,
        bool includeProbeNonce = false)
    {
        var result = await GetJsonAsync(
                client,
                queryUrl,
                includeProbeNonce,
                cancellationToken,
                new Dictionary<string, string?>
                {
                    ["where"] = "1=1",
                    ["outFields"] = fieldName,
                    ["returnDistinctValues"] = "true",
                    ["returnGeometry"] = "false",
                    ["f"] = "json"
                })
            .ConfigureAwait(false);

        if (!result.IsSuccess || result.Body is null)
        {
            return DistinctQueryResult.CreateFailure(result.ErrorMessage);
        }

        if (!TryGetPropertyCaseInsensitive(result.Body.Value, "features", out var features) || features.ValueKind != JsonValueKind.Array)
        {
            return DistinctQueryResult.CreateFailure("features array was missing from the response.");
        }

        var values = new HashSet<string>(StringComparer.Ordinal);
        foreach (var feature in features.EnumerateArray())
        {
            if (!TryGetPropertyCaseInsensitive(feature, "attributes", out var attributes))
            {
                continue;
            }

            if (TryGetPropertyCaseInsensitive(attributes, fieldName, out var value))
            {
                values.Add(NormalizeValue(value));
            }
        }

        return DistinctQueryResult.CreateSuccess(values.OrderBy(static value => value, StringComparer.Ordinal).ToArray());
    }

    private static async Task<StatisticsQueryResult> QueryStatisticsAsync(
        HttpClient client,
        string queryUrl,
        string fieldName,
        CancellationToken cancellationToken,
        bool includeProbeNonce = false)
    {
        var outStatistics = $"[{BuildStatisticDefinition(fieldName, "count", "parity_count")},{BuildStatisticDefinition(fieldName, "min", "parity_min")},{BuildStatisticDefinition(fieldName, "max", "parity_max")}]";
        var result = await GetJsonAsync(
                client,
                queryUrl,
                includeProbeNonce,
                cancellationToken,
                new Dictionary<string, string?>
                {
                    ["where"] = "1=1",
                    ["outStatistics"] = outStatistics,
                    ["f"] = "json"
                })
            .ConfigureAwait(false);

        if (!result.IsSuccess || result.Body is null)
        {
            return StatisticsQueryResult.CreateFailure(result.ErrorMessage);
        }

        if (!TryGetPropertyCaseInsensitive(result.Body.Value, "features", out var features) ||
            features.ValueKind != JsonValueKind.Array ||
            features.GetArrayLength() == 0)
        {
            return StatisticsQueryResult.CreateFailure("features array was missing from the statistics response.");
        }

        var attributes = features[0].GetProperty("attributes");
        return StatisticsQueryResult.CreateSuccess(
            ReadDoubleOrLong(attributes, "parity_count") ?? 0d,
            ReadDoubleOrLong(attributes, "parity_min"),
            ReadDoubleOrLong(attributes, "parity_max"));
    }

    private static async Task<GroupedCountQueryResult> QueryGroupedCountsAsync(
        HttpClient client,
        string queryUrl,
        string fieldName,
        CancellationToken cancellationToken,
        bool includeProbeNonce = false)
    {
        var result = await GetJsonAsync(
                client,
                queryUrl,
                includeProbeNonce,
                cancellationToken,
                new Dictionary<string, string?>
                {
                    ["where"] = "1=1",
                    ["groupByFieldsForStatistics"] = fieldName,
                    ["outStatistics"] = $"[{BuildStatisticDefinition(fieldName, "count", "parity_group_count")}]",
                    ["orderByFields"] = fieldName,
                    ["resultRecordCount"] = "25",
                    ["f"] = "json"
                })
            .ConfigureAwait(false);

        if (!result.IsSuccess || result.Body is null)
        {
            return GroupedCountQueryResult.CreateFailure(result.ErrorMessage);
        }

        if (!TryGetPropertyCaseInsensitive(result.Body.Value, "features", out var features) || features.ValueKind != JsonValueKind.Array)
        {
            return GroupedCountQueryResult.CreateFailure("features array was missing from grouped statistics response.");
        }

        var groups = new List<KeyValuePair<string, long>>();
        foreach (var feature in features.EnumerateArray())
        {
            if (!TryGetPropertyCaseInsensitive(feature, "attributes", out var attributes))
            {
                continue;
            }

            var key = TryGetPropertyCaseInsensitive(attributes, fieldName, out var fieldValue)
                ? NormalizeValue(fieldValue)
                : "<missing>";
            var count = ReadDoubleOrLong(attributes, "parity_group_count") ?? 0d;
            groups.Add(new KeyValuePair<string, long>(key, Convert.ToInt64(count, CultureInfo.InvariantCulture)));
        }

        var ordered = groups
            .OrderBy(static item => item.Key, StringComparer.Ordinal)
            .ThenBy(static item => item.Value)
            .ToArray();
        return GroupedCountQueryResult.CreateSuccess(ordered);
    }

    private static async Task<DateRangeQueryResult> QueryDateRangeAsync(
        HttpClient client,
        string queryUrl,
        string fieldName,
        CancellationToken cancellationToken,
        bool includeProbeNonce = false)
    {
        var result = await GetJsonAsync(
                client,
                queryUrl,
                includeProbeNonce,
                cancellationToken,
                new Dictionary<string, string?>
                {
                    ["where"] = "1=1",
                    ["outStatistics"] = $"[{BuildStatisticDefinition(fieldName, "min", "parity_date_min")},{BuildStatisticDefinition(fieldName, "max", "parity_date_max")}]",
                    ["f"] = "json"
                })
            .ConfigureAwait(false);

        if (!result.IsSuccess || result.Body is null)
        {
            return DateRangeQueryResult.CreateFailure(result.ErrorMessage);
        }

        if (!TryGetPropertyCaseInsensitive(result.Body.Value, "features", out var features) ||
            features.ValueKind != JsonValueKind.Array ||
            features.GetArrayLength() == 0)
        {
            return DateRangeQueryResult.CreateFailure("Date statistics response was missing.");
        }

        var attributes = features[0].GetProperty("attributes");
        return DateRangeQueryResult.CreateSuccess(
            ReadEpoch(attributes, "parity_date_min"),
            ReadEpoch(attributes, "parity_date_max"));
    }

    private static async Task<ErrorQueryResult> QueryInvalidTimeAsync(
        HttpClient client,
        string queryUrl,
        CancellationToken cancellationToken,
        bool includeProbeNonce = false)
    {
        var result = await GetJsonAsync(
                client,
                queryUrl,
                includeProbeNonce,
                cancellationToken,
                new Dictionary<string, string?>
                {
                    ["where"] = "1=1",
                    ["time"] = "not-a-time",
                    ["returnCountOnly"] = "true",
                    ["f"] = "json"
                })
            .ConfigureAwait(false);

        return new ErrorQueryResult(!result.IsSuccess, result.StatusCode, result.ErrorCode, result.ErrorMessage);
    }

    private static string BuildStatisticDefinition(string fieldName, string statisticType, string outputFieldName)
        => $"{{\"statisticType\":\"{statisticType}\",\"onStatisticField\":\"{EscapeJson(fieldName)}\",\"outStatisticFieldName\":\"{EscapeJson(outputFieldName)}\"}}";

    private static string EscapeJson(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private static MigrationComparisonCheck CreateFailedProbeCheck(
        string checkName,
        MigrationEvidenceLayerMapping mapping,
        string summary,
        string? detail) =>
        new()
        {
            CheckName = checkName,
            Status = MigrationEvidenceStatus.Fail,
            Scope = FormatScope(mapping),
            Summary = summary,
            Notes = string.IsNullOrWhiteSpace(detail) ? [] : [detail]
        };

    private static MigrationComparisonCheck CreateNotApplicableCheck(
        string checkName,
        MigrationEvidenceLayerMapping mapping,
        string summary) =>
        new()
        {
            CheckName = checkName,
            Status = MigrationEvidenceStatus.NotApplicable,
            Scope = FormatScope(mapping),
            Summary = summary
        };

    private static string FormatScope(MigrationEvidenceLayerMapping mapping)
        => $"{mapping.SourceLayerId}{ScopeDelimiter}{mapping.TargetLayerId}";

    private static string[] NormalizeArray(IEnumerable<string>? values)
        => (values ?? [])
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string[] ParseDelimitedOrArray(JsonElement metadata, string propertyName)
    {
        if (!TryGetPropertyCaseInsensitive(metadata, propertyName, out var value))
        {
            return [];
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString()?
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .OrderBy(static item => item, StringComparer.OrdinalIgnoreCase)
                .ToArray() ?? [];
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            return value.EnumerateArray()
                .Where(static item => item.ValueKind == JsonValueKind.String)
                .Select(static item => item.GetString()!)
                .OrderBy(static item => item, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        return [];
    }

    private static bool SupportsQueryFormat(JsonElement metadata, string format)
        => ParseDelimitedOrArray(metadata, "supportedQueryFormats")
            .Contains(format, StringComparer.OrdinalIgnoreCase);

    private static bool SupportsResultTypeQuery(JsonElement metadata)
    {
        if (!TryGetPropertyCaseInsensitive(metadata, "advancedQueryCapabilities", out var advanced) ||
            advanced.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        return ReadOptionalBool(advanced, "supportsQueryWithResultType") ?? false;
    }

    private static string? ResolveObjectIdField(JsonElement metadata)
    {
        var field = ReadOptionalString(metadata, "objectIdField");
        if (!string.IsNullOrWhiteSpace(field))
        {
            return field;
        }

        if (!TryGetPropertyCaseInsensitive(metadata, "fields", out var fields) || fields.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var candidate in fields.EnumerateArray())
        {
            if (string.Equals(ReadOptionalString(candidate, "type"), "esriFieldTypeOID", StringComparison.OrdinalIgnoreCase))
            {
                return ReadOptionalString(candidate, "name");
            }
        }

        return null;
    }

    private static MigrationEvidenceFieldSnapshot[] ReadFieldSnapshots(JsonElement? metadata)
    {
        if (metadata is not { } body ||
            !TryGetPropertyCaseInsensitive(body, "fields", out var fields) ||
            fields.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return fields.EnumerateArray()
            .Select(field => new MigrationEvidenceFieldSnapshot
            {
                Name = ReadOptionalString(field, "name") ?? string.Empty,
                CanonicalName = (ReadOptionalString(field, "name") ?? string.Empty).SanitizeFieldName(),
                Type = ReadOptionalString(field, "type") ?? string.Empty,
                Nullable = ReadOptionalBool(field, "nullable") ?? true
            })
            .Where(static field => !string.IsNullOrWhiteSpace(field.Name))
            .ToArray();
    }

    private static MigrationEvidenceExtentSnapshot? ReadExtent(JsonElement? metadata)
    {
        if (metadata is not { } body ||
            !TryGetPropertyCaseInsensitive(body, "extent", out var extent) ||
            extent.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var minX = ReadOptionalDouble(extent, "xmin");
        var minY = ReadOptionalDouble(extent, "ymin");
        var maxX = ReadOptionalDouble(extent, "xmax");
        var maxY = ReadOptionalDouble(extent, "ymax");
        if (minX is null || minY is null || maxX is null || maxY is null)
        {
            return null;
        }

        return new MigrationEvidenceExtentSnapshot
        {
            MinX = minX.Value,
            MinY = minY.Value,
            MaxX = maxX.Value,
            MaxY = maxY.Value,
            SpatialReferenceWkid = ReadSpatialReferenceWkid(extent)
        };
    }

    private static int? ReadSpatialReferenceWkid(JsonElement? element)
    {
        if (element is not { } body)
        {
            return null;
        }

        if (TryGetPropertyCaseInsensitive(body, "spatialReference", out var spatialReference) &&
            spatialReference.ValueKind == JsonValueKind.Object)
        {
            return ReadOptionalInt(spatialReference, "wkid") ?? ReadOptionalInt(spatialReference, "latestWkid");
        }

        return ReadOptionalInt(body, "spatialReferenceWkid");
    }

    private static string? ReadOptionalString(JsonElement? element, string propertyName)
    {
        if (element is not { } body || !TryGetPropertyCaseInsensitive(body, propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    }

    private static bool? ReadOptionalBool(JsonElement? element, string propertyName)
    {
        if (element is not { } body || !TryGetPropertyCaseInsensitive(body, propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(property.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static int? ReadOptionalInt(JsonElement? element, string propertyName)
    {
        if (element is not { } body || !TryGetPropertyCaseInsensitive(body, propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var parsed) => parsed,
            JsonValueKind.String when int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }

    private static long? ReadOptionalLong(JsonElement? element, string propertyName)
    {
        if (element is not { } body || !TryGetPropertyCaseInsensitive(body, propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt64(out var parsed) => parsed,
            JsonValueKind.String when long.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }

    private static double? ReadOptionalDouble(JsonElement? element, string propertyName)
    {
        if (element is not { } body || !TryGetPropertyCaseInsensitive(body, propertyName, out var property))
        {
            return null;
        }

        return ReadDouble(property);
    }

    private static double? ReadDoubleOrLong(JsonElement element, string propertyName)
        => TryGetPropertyCaseInsensitive(element, propertyName, out var property)
            ? ReadDouble(property)
            : null;

    private static double? ReadDouble(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetDouble(out var parsed) => parsed,
            JsonValueKind.String when double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }

    private static long? ReadEpoch(JsonElement attributes, string propertyName)
    {
        if (!TryGetPropertyCaseInsensitive(attributes, propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt64(out var parsed) => parsed,
            JsonValueKind.String when long.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }

    private static bool TryGetPropertyCaseInsensitive(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out value))
        {
            return true;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static bool TryGetInt64(JsonElement element, out long value)
    {
        value = default;
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out value))
        {
            return true;
        }

        if (element.ValueKind == JsonValueKind.String &&
            long.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        return false;
    }

    private static string NormalizeValue(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => "<null>",
            JsonValueKind.String => element.GetString() ?? "<null>",
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number => element.GetRawText(),
            _ => element.GetRawText()
        };

    private static int CountDistinct(long[] values)
        => values.Distinct().Count();

    private static bool NearlyEqual(double? left, double? right, double tolerance = 1e-9d)
    {
        if (left is null && right is null)
        {
            return true;
        }

        if (left is null || right is null)
        {
            return false;
        }

        return Math.Abs(left.Value - right.Value) <= tolerance;
    }

    private static string FormatDouble(double? value)
        => value?.ToString("G17", CultureInfo.InvariantCulture) ?? "<null>";

    private static bool IsStringFieldType(string fieldType)
        => string.Equals(fieldType, "esriFieldTypeString", StringComparison.OrdinalIgnoreCase);

    private static bool IsNumericFieldType(string fieldType)
        => string.Equals(fieldType, "esriFieldTypeInteger", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(fieldType, "esriFieldTypeSmallInteger", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(fieldType, "esriFieldTypeOID", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(fieldType, "esriFieldTypeDouble", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(fieldType, "esriFieldTypeSingle", StringComparison.OrdinalIgnoreCase);

    private static bool IsDateFieldType(string fieldType)
        => string.Equals(fieldType, "esriFieldTypeDate", StringComparison.OrdinalIgnoreCase);

    private static int GetStatusClass(HttpStatusCode statusCode)
        => ((int)statusCode) / 100;

    private static MigrationEvidenceStatus AggregateStatus(IEnumerable<MigrationComparisonCheck> checks)
    {
        var statuses = checks.Select(static check => check.Status).ToArray();
        if (statuses.Length == 0)
        {
            return MigrationEvidenceStatus.NotApplicable;
        }

        if (statuses.Contains(MigrationEvidenceStatus.Fail))
        {
            return MigrationEvidenceStatus.Fail;
        }

        if (statuses.Contains(MigrationEvidenceStatus.Warning))
        {
            return MigrationEvidenceStatus.Warning;
        }

        if (statuses.All(static status => status == MigrationEvidenceStatus.NotApplicable))
        {
            return MigrationEvidenceStatus.NotApplicable;
        }

        return MigrationEvidenceStatus.Pass;
    }

    private static string ComputeReportHash(MigrationEvidenceReport report)
    {
        var canonicalReport = report with { ReportHash = string.Empty };
        var json = JsonSerializer.Serialize(canonicalReport, MigrationEvidenceDomainJsonContext.Default.MigrationEvidenceReport);
        return ComputeSha256Hex(json);
    }

    private static string ComputeCanonicalJsonHash(JsonElement element)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonicalJson(writer, element);
        }

        return ComputeSha256Hex(stream.ToArray());
    }

    private static void WriteCanonicalJson(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(static item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalJson(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonicalJson(writer, item);
                }

                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static string ComputeSha256Hex(string value)
        => ComputeSha256Hex(Encoding.UTF8.GetBytes(value));

    private static string ComputeSha256Hex(byte[] bytes)
        => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed record LayerWorkItem(
        MigrationEvidenceLayerMapping Mapping,
        MigrationEvidenceLayerSnapshot Snapshot,
        RemoteJsonResult Result);

    internal sealed record FieldMappingSet(
        FieldMappingEntry[] Entries,
        FieldMappingEntry? StringField,
        FieldMappingEntry? NumericField,
        FieldMappingEntry? DateField);

    internal sealed record FieldMappingEntry(
        string SourceField,
        string TargetField,
        string CanonicalField,
        string SourceType);

    internal enum FeatureRowFieldOrigin
    {
        Source,
        Target
    }

    private sealed record DataCheckResult(
        IReadOnlyList<MigrationComparisonCheck> Checks,
        MigrationEvidenceStatus? GeodesyStatus);

    private sealed record SpatialExtentCheckResult(
        MigrationComparisonCheck Check,
        MigrationEvidenceStatus GeodesyStatus);

    private sealed record PreflightProbeResult(MigrationEvidenceOperationalSnapshot Snapshot);

    private readonly record struct RemoteJsonResult(
        bool IsSuccess,
        HttpStatusCode StatusCode,
        string? ErrorMessage,
        int? ErrorCode,
        JsonElement? Body,
        bool ExceededTransferLimit)
    {
        public static RemoteJsonResult CreateFailure(HttpStatusCode statusCode, string? errorMessage, int? errorCode, JsonElement? body)
            => new(false, statusCode, errorMessage, errorCode, body, false);
    }

    private readonly record struct CountQueryResult(bool IsSuccess, long Count, string? ErrorMessage)
    {
        public static CountQueryResult CreateSuccess(long count) => new(true, count, null);
        public static CountQueryResult CreateFailure(string? errorMessage) => new(false, 0L, errorMessage);
    }

    private readonly record struct ObjectIdQueryResult(bool IsSuccess, long[] ObjectIds, string? ErrorMessage)
    {
        public static ObjectIdQueryResult CreateSuccess(long[] objectIds) => new(true, objectIds, null);
        public static ObjectIdQueryResult CreateFailure(string? errorMessage) => new(false, [], errorMessage);
    }

    private readonly record struct DistinctQueryResult(bool IsSuccess, string[] Values, string? ErrorMessage)
    {
        public static DistinctQueryResult CreateSuccess(string[] values) => new(true, values, null);
        public static DistinctQueryResult CreateFailure(string? errorMessage) => new(false, [], errorMessage);
    }

    private readonly record struct StatisticsQueryResult(bool IsSuccess, double Count, double? Min, double? Max, string? ErrorMessage)
    {
        public static StatisticsQueryResult CreateSuccess(double count, double? min, double? max) => new(true, count, min, max, null);
        public static StatisticsQueryResult CreateFailure(string? errorMessage) => new(false, 0d, null, null, errorMessage);
    }

    private readonly record struct GroupedCountQueryResult(bool IsSuccess, KeyValuePair<string, long>[] Groups, string? ErrorMessage)
    {
        public static GroupedCountQueryResult CreateSuccess(KeyValuePair<string, long>[] groups) => new(true, groups, null);
        public static GroupedCountQueryResult CreateFailure(string? errorMessage) => new(false, [], errorMessage);
    }

    private readonly record struct DateRangeQueryResult(bool IsSuccess, long? MinEpochMs, long? MaxEpochMs, string? ErrorMessage)
    {
        public static DateRangeQueryResult CreateSuccess(long? minEpochMs, long? maxEpochMs) => new(true, minEpochMs, maxEpochMs, null);
        public static DateRangeQueryResult CreateFailure(string? errorMessage) => new(false, null, null, errorMessage);
    }

    private readonly record struct ErrorQueryResult(bool IsError, HttpStatusCode StatusCode, int? ErrorCode, string? ErrorMessage);

    private static partial class Log
    {
        [LoggerMessage(9120, LogLevel.Information, "Generating migration evidence for {SourceServiceUrl} -> {TargetServiceName} ({CutoverProfile}).")]
        public static partial void GenerationStarted(ILogger logger, string sourceServiceUrl, string targetServiceName, MigrationCutoverProfile cutoverProfile);

        [LoggerMessage(9121, LogLevel.Information, "Generated migration evidence report {ReportId} with readiness {Readiness} and hash {ReportHash}.")]
        public static partial void GenerationCompleted(ILogger logger, Guid reportId, MigrationReadinessState readiness, string reportHash);

        [LoggerMessage(9122, LogLevel.Warning, "Deploy preflight probe failed during migration evidence generation.")]
        public static partial void PreflightProbeFailed(ILogger logger, Exception exception);
    }
}
