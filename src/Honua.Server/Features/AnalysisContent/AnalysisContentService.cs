// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.AnalysisContent;
using Honua.Core.Features.AnalysisContent.Abstractions;
using Honua.Core.Features.AnalysisContent.Domain;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.NlQuery.Services;
using Honua.Core.Features.Query;
using Honua.Core.Queries.Filters;
using Honua.Server.Features.Geoprocessing;

namespace Honua.Server.Features.AnalysisContent;

internal sealed partial class AnalysisContentService(
    IAnalysisContentStore store,
    ILayerCatalog layerCatalog,
    IQueryProcessor queryProcessor,
    IFeatureReader featureReader,
    IGeoprocessingJobService geoprocessingJobService,
    IEnumerable<IExecutionLogStore> logStores,
    TimeProvider timeProvider,
    ILogger<AnalysisContentService> logger) : IAnalysisContentService
{
    private const int DefaultPreviewLimit = 25;
    private const int MaxPreviewLimit = 200;
    private const int DefaultLogLimit = 100;
    private const int MaxLogLimit = 200;
    private const int MaxDiagnosticLength = 512;
    private static readonly TimeSpan PreviewRetention = TimeSpan.FromHours(1);

    private readonly IExecutionLogStore? _logStore = logStores.FirstOrDefault();

    public async Task<AnalysisContentItemResult> CreateItemAsync(
        CreateAnalysisContentItemCommand command,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken)
    {
        ValidateCreate(command);

        var now = timeProvider.GetUtcNow();
        var itemId = CreateItemId();
        var version = CreateVersion(
            itemId,
            1,
            command.Kind,
            command.SavedQuery,
            command.AnalysisPackage,
            basedOnVersionId: null,
            createdFromJobId: null,
            createdFromArtifactIds: [],
            createdBy: ResolveActor(principal),
            now);

        var item = new AnalysisContentItem
        {
            ItemId = itemId,
            Kind = command.Kind,
            Name = NormalizeName(command.Name),
            Title = NormalizeOptional(command.Title),
            OwnerId = ResolveActor(principal),
            CurrentVersion = version.Version,
            CurrentVersionId = version.VersionId,
            CreatedAt = now,
            UpdatedAt = now,
            CreatedBy = ResolveActor(principal)
        };

        var stored = await store.CreateItemAsync(item, version, cancellationToken).ConfigureAwait(false);
        Log.ContentItemCreated(logger, stored.ItemId, stored.Kind.ToString(), stored.CurrentVersion);
        return new AnalysisContentItemResult(stored, version);
    }

    public async Task<AnalysisContentItemResult> GetItemAsync(
        string itemId,
        CancellationToken cancellationToken)
    {
        var item = await GetRequiredItemAsync(itemId, cancellationToken).ConfigureAwait(false);
        var version = await GetRequiredVersionAsync(itemId, null, cancellationToken).ConfigureAwait(false);
        return new AnalysisContentItemResult(item, version);
    }

    public async Task<AnalysisContentVersionResult> GetVersionAsync(
        string itemId,
        int? version,
        CancellationToken cancellationToken)
    {
        var item = await GetRequiredItemAsync(itemId, cancellationToken).ConfigureAwait(false);
        var contentVersion = await GetRequiredVersionAsync(itemId, version, cancellationToken).ConfigureAwait(false);
        return new AnalysisContentVersionResult(item, contentVersion);
    }

    public async Task<AnalysisContentVersionResult> AddVersionAsync(
        string itemId,
        CreateAnalysisContentVersionCommand command,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken)
    {
        var item = await GetRequiredItemAsync(itemId, cancellationToken).ConfigureAwait(false);
        var latest = await GetRequiredVersionAsync(itemId, null, cancellationToken).ConfigureAwait(false);
        var nextVersion = latest.Version + 1;
        var now = timeProvider.GetUtcNow();

        var version = CreateVersion(
            itemId,
            nextVersion,
            item.Kind,
            command.SavedQuery,
            command.AnalysisPackage,
            NormalizeOptional(command.BasedOnVersionId) ?? latest.VersionId,
            NormalizeOptional(command.CreatedFromJobId),
            command.CreatedFromArtifactIds ?? [],
            ResolveActor(principal),
            now);

        ValidatePayload(item.Kind, version.SavedQuery, version.AnalysisPackage);

        var stored = await store.AddVersionAsync(itemId, version, cancellationToken).ConfigureAwait(false);
        var updatedItem = await GetRequiredItemAsync(itemId, cancellationToken).ConfigureAwait(false);
        Log.ContentVersionCreated(logger, itemId, stored.Version);
        return new AnalysisContentVersionResult(updatedItem, stored);
    }

    public async Task<SavedQueryPreviewResult> PreviewSavedQueryAsync(
        string itemId,
        int version,
        int? limit,
        CancellationToken cancellationToken)
    {
        var contentVersion = await GetRequiredVersionAsync(itemId, version, cancellationToken).ConfigureAwait(false);
        var savedQuery = contentVersion.SavedQuery
            ?? throw new AnalysisContentValidationException("The requested version is not a saved query.");

        var layer = await layerCatalog.GetLayerAsync(savedQuery.LayerId, cancellationToken).ConfigureAwait(false)
            ?? throw new AnalysisContentNotFoundException($"Layer '{savedQuery.LayerId}' was not found.");

        var previewLimit = ResolveLimit(limit ?? savedQuery.PreviewLimit, DefaultPreviewLimit, MaxPreviewLimit);
        QueryFilter? filter = null;
        if (savedQuery.FilterPlan is { Clauses.Length: > 0 } filterPlan)
        {
            var compiled = FilterPlanCompiler.Compile(filterPlan, layer);
            if (!compiled.IsSuccess || compiled.Expression is null)
            {
                throw new AnalysisContentValidationException(
                    compiled.ErrorMessage ?? "Saved query filter plan could not be compiled.");
            }

            filter = QueryFilter.FromExpression(
                compiled.Expression,
                new FilterSource("saved-query", FilterLanguage.Cql2Json, "AnalysisContent"));
        }

        var query = new UnifiedQuery
        {
            Filter = filter,
            Limit = previewLimit,
            OutFields = savedQuery.OutFields.Count > 0
                ? ImmutableArray.CreateRange(savedQuery.OutFields)
                : null,
            OutputCrs = savedQuery.OutputSrid.HasValue
                ? QueryCrs.Create(savedQuery.OutputSrid.Value)
                : null
        };

        var validation = queryProcessor.ValidateQuery(query, layer);
        if (!validation.IsValid)
        {
            throw new AnalysisContentValidationException(
                validation.ErrorMessage ?? "Saved query preview request is invalid.");
        }

        var optimized = queryProcessor.OptimizeQuery(query, layer);
        var featureQuery = queryProcessor.ToFeatureQuery(optimized, layer);
        var result = await featureReader.QueryAsync(layer.Id, featureQuery, cancellationToken).ConfigureAwait(false);

        var artifactId = CreatePreviewArtifactId();
        var binding = new ArtifactBindingRef
        {
            ArtifactId = artifactId,
            SourceItemId = itemId,
            SourceVersion = contentVersion.Version,
            SourceVersionId = contentVersion.VersionId,
            Role = "preview",
            TargetKind = "map",
            TargetSlot = "source"
        };

        var now = timeProvider.GetUtcNow();
        await store.UpsertArtifactAsync(new ResultArtifactRecord
        {
            ArtifactId = artifactId,
            ResultPackageId = $"{itemId}:v{contentVersion.Version}:preview",
            JobId = "preview",
            SourceItemId = itemId,
            SourceVersion = contentVersion.Version,
            SourceVersionId = contentVersion.VersionId,
            Kind = ArtifactKind.FeatureLayer,
            Label = $"{savedQuery.ServiceName ?? layer.Name} preview",
            Uri = $"honua://analysis/artifacts/{artifactId}",
            ContentType = "application/json",
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["layerId"] = savedQuery.LayerId.ToString(CultureInfo.InvariantCulture),
                ["previewLimit"] = previewLimit.ToString(CultureInfo.InvariantCulture)
            },
            Provenance = BuildSourceProvenance(contentVersion, savedQuery.OutputSrid, savedQuery.Units),
            RetentionState = ResultArtifactRetentionState.Preview,
            CreatedAt = now,
            ExpiresAt = now.Add(PreviewRetention)
        }, cancellationToken).ConfigureAwait(false);

        Log.SavedQueryPreviewed(logger, itemId, contentVersion.Version, result.Items.Length);

        return new SavedQueryPreviewResult
        {
            PreviewArtifactId = artifactId,
            ItemId = itemId,
            Version = contentVersion.Version,
            LayerId = savedQuery.LayerId,
            Features = result.Items.Select(SavedQueryPreviewFeature.FromFeature).ToArray(),
            TotalCount = result.TotalCount,
            ExceededPreviewLimit = result.HasMoreResults || result.TotalCount > previewLimit,
            Binding = binding
        };
    }

    public async Task<AnalysisContentJobResult> SubmitAnalysisPackageAsync(
        string itemId,
        int version,
        RunAnalysisContentVersionCommand command,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken)
    {
        var contentVersion = await GetRequiredVersionAsync(itemId, version, cancellationToken).ConfigureAwait(false);
        var package = contentVersion.AnalysisPackage
            ?? throw new AnalysisContentValidationException("The requested version is not an analysis package.");

        var metadata = BuildJobMetadata(contentVersion, package, command.Parameters);
        var job = await geoprocessingJobService.SubmitJobAsync(
            package.Plan,
            command.IdempotencyKey,
            principal,
            metadata,
            cancellationToken).ConfigureAwait(false);

        Log.AnalysisPackageSubmitted(logger, itemId, version, job.OperationId);
        return new AnalysisContentJobResult(job, contentVersion);
    }

    public async Task<AnalysisContentJobResult> RerunAnalysisPackageAsync(
        string itemId,
        int version,
        RerunAnalysisContentVersionCommand command,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken)
    {
        var contentVersion = await GetRequiredVersionAsync(itemId, version, cancellationToken).ConfigureAwait(false);
        var package = contentVersion.AnalysisPackage
            ?? throw new AnalysisContentValidationException("The requested version is not an analysis package.");

        var submittedVersion = contentVersion;
        if (command.ParameterOverrides is { Count: > 0 })
        {
            var merged = new Dictionary<string, string>(package.Parameters, StringComparer.Ordinal);
            foreach (var pair in command.ParameterOverrides)
            {
                if (!string.IsNullOrWhiteSpace(pair.Key))
                {
                    merged[pair.Key] = pair.Value;
                }
            }

            var next = new CreateAnalysisContentVersionCommand(
                SavedQuery: null,
                AnalysisPackage: package with { Parameters = merged },
                BasedOnVersionId: contentVersion.VersionId,
                CreatedFromJobId: NormalizeOptional(command.RerunOfJobId),
                CreatedFromArtifactIds: []);
            var result = await AddVersionAsync(itemId, next, principal, cancellationToken).ConfigureAwait(false);
            submittedVersion = result.Version;
            package = submittedVersion.AnalysisPackage!;
        }

        var metadata = BuildJobMetadata(submittedVersion, package, runtimeParameters: null);
        if (!string.IsNullOrWhiteSpace(command.RerunOfJobId))
        {
            metadata[AnalysisContentMetadataKeys.RerunOfJobId] = command.RerunOfJobId.Trim();
        }

        if (!string.IsNullOrWhiteSpace(command.RerunOfResultPackageId))
        {
            metadata[AnalysisContentMetadataKeys.RerunOfResultPackageId] = command.RerunOfResultPackageId.Trim();
        }

        var job = await geoprocessingJobService.SubmitJobAsync(
            package.Plan,
            command.IdempotencyKey,
            principal,
            metadata,
            cancellationToken).ConfigureAwait(false);

        Log.AnalysisPackageRerunSubmitted(logger, itemId, submittedVersion.Version, job.OperationId);
        return new AnalysisContentJobResult(job, submittedVersion);
    }

    public async Task<ResultArtifactRecord> GetArtifactAsync(
        string artifactId,
        CancellationToken cancellationToken)
    {
        var artifact = await store.GetArtifactAsync(artifactId, cancellationToken).ConfigureAwait(false);
        return artifact ?? throw new AnalysisContentNotFoundException($"Artifact '{artifactId}' was not found.");
    }

    public async Task<AnalysisJobLogs> GetJobLogsAsync(
        string jobId,
        int? limit,
        CancellationToken cancellationToken)
    {
        var resolvedLimit = ResolveLimit(limit, DefaultLogLimit, MaxLogLimit);
        if (_logStore is null)
        {
            return new AnalysisJobLogs
            {
                JobId = jobId,
                Entries = [],
                TotalCount = 0,
                Truncated = false
            };
        }

        var logs = await _logStore.GetLogsAsync(jobId, cancellationToken).ConfigureAwait(false);
        var bounded = logs.TakeLast(resolvedLimit)
            .Select(entry => new AnalysisJobLogEntry
            {
                Timestamp = entry.Timestamp,
                Level = entry.Level,
                Message = SanitizeDiagnostic(entry.Message),
                Phase = SanitizePhase(entry.Phase),
                Metadata = SanitizeMetadata(entry.Metadata)
            })
            .ToArray();

        return new AnalysisJobLogs
        {
            JobId = jobId,
            Entries = bounded,
            TotalCount = logs.Count,
            Truncated = logs.Count > bounded.Length
        };
    }

    public async Task<AnalysisJobFailure> GetJobFailureAsync(
        string jobId,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken)
    {
        var job = await geoprocessingJobService.GetJobAsync(jobId, principal, cancellationToken).ConfigureAwait(false);

        return job.Status switch
        {
            ExecutionJobStatus.Failed => new AnalysisJobFailure
            {
                JobId = job.OperationId,
                Classification = ClassifyFailure(job.ErrorMessage),
                Message = SanitizeFailureMessage(job.ErrorMessage),
                IsTerminal = true,
                FailedAt = job.CompletedAt
            },
            ExecutionJobStatus.Cancelled => new AnalysisJobFailure
            {
                JobId = job.OperationId,
                Classification = AnalysisJobFailureClassification.Cancelled,
                Message = "The analysis job was cancelled.",
                IsTerminal = true,
                FailedAt = job.CompletedAt
            },
            _ => throw new AnalysisContentConflictException("The requested job has not failed.")
        };
    }

    private async Task<AnalysisContentItem> GetRequiredItemAsync(
        string itemId,
        CancellationToken cancellationToken)
    {
        var item = await store.GetItemAsync(itemId, cancellationToken).ConfigureAwait(false);
        return item ?? throw new AnalysisContentNotFoundException($"Analysis content item '{itemId}' was not found.");
    }

    private async Task<AnalysisContentVersion> GetRequiredVersionAsync(
        string itemId,
        int? version,
        CancellationToken cancellationToken)
    {
        var contentVersion = await store.GetVersionAsync(itemId, version, cancellationToken).ConfigureAwait(false);
        return contentVersion ?? throw new AnalysisContentNotFoundException(
            version.HasValue
                ? $"Analysis content item '{itemId}' version '{version.Value}' was not found."
                : $"Analysis content item '{itemId}' has no versions.");
    }

    private static void ValidateCreate(CreateAnalysisContentItemCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.Name))
        {
            throw new AnalysisContentValidationException("Analysis content name is required.");
        }

        ValidatePayload(command.Kind, command.SavedQuery, command.AnalysisPackage);
    }

    private static void ValidatePayload(
        AnalysisContentKind kind,
        SavedQueryContent? savedQuery,
        AnalysisPackageContent? analysisPackage)
    {
        switch (kind)
        {
            case AnalysisContentKind.SavedQuery:
                if (savedQuery is null || analysisPackage is not null)
                {
                    throw new AnalysisContentValidationException("Saved-query content requires a savedQuery payload only.");
                }

                if (savedQuery.LayerId < 0)
                {
                    throw new AnalysisContentValidationException("Saved-query layerId must be non-negative.");
                }

                break;
            case AnalysisContentKind.AnalysisPackage:
                if (analysisPackage is null || savedQuery is not null)
                {
                    throw new AnalysisContentValidationException("Analysis-package content requires an analysisPackage payload only.");
                }

                if (analysisPackage.Plan.Steps.Count == 0)
                {
                    throw new AnalysisContentValidationException("Analysis package plan must contain at least one step.");
                }

                break;
            default:
                throw new AnalysisContentValidationException("Unsupported analysis content kind.");
        }
    }

    private static AnalysisContentVersion CreateVersion(
        string itemId,
        int version,
        AnalysisContentKind kind,
        SavedQueryContent? savedQuery,
        AnalysisPackageContent? analysisPackage,
        string? basedOnVersionId,
        string? createdFromJobId,
        IReadOnlyList<string> createdFromArtifactIds,
        string? createdBy,
        DateTimeOffset now)
    {
        ValidatePayload(kind, savedQuery, analysisPackage);
        return new AnalysisContentVersion
        {
            VersionId = $"{itemId}:v{version.ToString(CultureInfo.InvariantCulture)}",
            ItemId = itemId,
            Version = version,
            Kind = kind,
            SavedQuery = savedQuery,
            AnalysisPackage = analysisPackage,
            ContentHash = ComputeContentHash(kind, savedQuery, analysisPackage),
            BasedOnVersionId = basedOnVersionId,
            CreatedFromJobId = createdFromJobId,
            CreatedFromArtifactIds = createdFromArtifactIds,
            CreatedAt = now,
            CreatedBy = createdBy
        };
    }

    private static string ComputeContentHash(
        AnalysisContentKind kind,
        SavedQueryContent? savedQuery,
        AnalysisPackageContent? analysisPackage)
    {
        byte[] payload = kind == AnalysisContentKind.SavedQuery
            ? JsonSerializer.SerializeToUtf8Bytes(savedQuery, AnalysisContentJsonContext.Default.SavedQueryContent)
            : JsonSerializer.SerializeToUtf8Bytes(analysisPackage, AnalysisContentJsonContext.Default.AnalysisPackageContent);

        var hash = SHA256.HashData(payload);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static Dictionary<string, string> BuildJobMetadata(
        AnalysisContentVersion version,
        AnalysisPackageContent package,
        IReadOnlyDictionary<string, string>? runtimeParameters)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AnalysisContentMetadataKeys.ItemId] = version.ItemId,
            [AnalysisContentMetadataKeys.Version] = version.Version.ToString(CultureInfo.InvariantCulture),
            [AnalysisContentMetadataKeys.VersionId] = version.VersionId,
            [AnalysisContentMetadataKeys.Kind] = version.Kind.ToString(),
        };

        if (package.SpatialReferenceId.HasValue)
        {
            metadata[AnalysisContentMetadataKeys.SourceSrid] =
                package.SpatialReferenceId.Value.ToString(CultureInfo.InvariantCulture);
        }

        if (!string.IsNullOrWhiteSpace(package.Units))
        {
            metadata[AnalysisContentMetadataKeys.SourceUnits] = package.Units.Trim();
        }

        foreach (var pair in package.Parameters)
        {
            if (!string.IsNullOrWhiteSpace(pair.Key))
            {
                metadata[$"analysis.content.parameter.{pair.Key}"] = pair.Value;
            }
        }

        if (runtimeParameters is not null)
        {
            foreach (var pair in runtimeParameters)
            {
                if (!string.IsNullOrWhiteSpace(pair.Key))
                {
                    metadata[$"analysis.content.runtime_parameter.{pair.Key}"] = pair.Value;
                }
            }
        }

        return metadata;
    }

    private static Dictionary<string, string> BuildSourceProvenance(
        AnalysisContentVersion version,
        int? srid,
        string? units)
    {
        var provenance = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AnalysisContentMetadataKeys.ItemId] = version.ItemId,
            [AnalysisContentMetadataKeys.Version] = version.Version.ToString(CultureInfo.InvariantCulture),
            [AnalysisContentMetadataKeys.VersionId] = version.VersionId,
            [AnalysisContentMetadataKeys.Kind] = version.Kind.ToString()
        };

        if (srid.HasValue)
        {
            provenance[AnalysisContentMetadataKeys.SourceSrid] = srid.Value.ToString(CultureInfo.InvariantCulture);
        }

        if (!string.IsNullOrWhiteSpace(units))
        {
            provenance[AnalysisContentMetadataKeys.SourceUnits] = units.Trim();
        }

        return provenance;
    }

    private static int ResolveLimit(int? limit, int defaultLimit, int maxLimit)
    {
        var resolved = limit.GetValueOrDefault(defaultLimit);
        if (resolved <= 0)
        {
            throw new AnalysisContentValidationException("Limit must be greater than zero.");
        }

        return Math.Min(resolved, maxLimit);
    }

    private static AnalysisJobFailureClassification ClassifyFailure(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return AnalysisJobFailureClassification.Unknown;
        }

        if (message.Contains("validation", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("invalid", StringComparison.OrdinalIgnoreCase))
        {
            return AnalysisJobFailureClassification.ValidationFailed;
        }

        if (message.Contains("unauthor", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("forbidden", StringComparison.OrdinalIgnoreCase))
        {
            return AnalysisJobFailureClassification.AuthorizationDenied;
        }

        if (message.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("timed out", StringComparison.OrdinalIgnoreCase))
        {
            return AnalysisJobFailureClassification.TimedOut;
        }

        if (message.Contains("artifact", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("output", StringComparison.OrdinalIgnoreCase))
        {
            return AnalysisJobFailureClassification.ArtifactOutputFailed;
        }

        return AnalysisJobFailureClassification.ExecutionFailed;
    }

    private static string SanitizeFailureMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "The analysis job failed.";
        }

        var sanitized = SanitizeDiagnostic(message);
        return string.IsNullOrWhiteSpace(sanitized)
            ? "The analysis job failed."
            : sanitized;
    }

    private static string SanitizeDiagnostic(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var sanitized = value.ReplaceLineEndings(" ").Trim();
        if (sanitized.Contains(" at ", StringComparison.Ordinal) ||
            sanitized.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) ||
            sanitized.Contains("connection string", StringComparison.OrdinalIgnoreCase) ||
            sanitized.Contains("password", StringComparison.OrdinalIgnoreCase))
        {
            sanitized = "The analysis job failed. See server logs for details.";
        }

        return sanitized.Length <= MaxDiagnosticLength
            ? sanitized
            : sanitized[..MaxDiagnosticLength];
    }

    private static string? SanitizePhase(string? value)
    {
        var sanitized = SanitizeDiagnostic(value);
        return string.IsNullOrWhiteSpace(sanitized) ? null : sanitized;
    }

    private static Dictionary<string, string>? SanitizeMetadata(IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is null || metadata.Count == 0)
        {
            return null;
        }

        return metadata
            .Where(pair => !pair.Key.Contains("password", StringComparison.OrdinalIgnoreCase)
                           && !pair.Key.Contains("secret", StringComparison.OrdinalIgnoreCase)
                           && !pair.Key.Contains("connection", StringComparison.OrdinalIgnoreCase))
            .Take(20)
            .ToDictionary(pair => pair.Key, pair => SanitizeDiagnostic(pair.Value), StringComparer.Ordinal);
    }

    private static string CreateItemId() => $"analysis-content-{Guid.NewGuid():N}";

    private static string CreatePreviewArtifactId() => $"analysis-preview-{Guid.NewGuid():N}";

    private static string NormalizeName(string value) => value.Trim();

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? ResolveActor(ClaimsPrincipal principal)
        => principal.Identity?.Name
           ?? principal.FindFirstValue(ClaimTypes.NameIdentifier)
           ?? principal.FindFirstValue("sub");

    private static partial class Log
    {
        [LoggerMessage(12001, LogLevel.Information, "Created analysis content item {ItemId} kind {Kind} version {Version}")]
        public static partial void ContentItemCreated(ILogger logger, string itemId, string kind, int version);

        [LoggerMessage(12002, LogLevel.Information, "Created analysis content item {ItemId} version {Version}")]
        public static partial void ContentVersionCreated(ILogger logger, string itemId, int version);

        [LoggerMessage(12003, LogLevel.Information, "Previewed saved query {ItemId} version {Version} with {FeatureCount} features")]
        public static partial void SavedQueryPreviewed(ILogger logger, string itemId, int version, int featureCount);

        [LoggerMessage(12004, LogLevel.Information, "Submitted analysis package {ItemId} version {Version} as job {JobId}")]
        public static partial void AnalysisPackageSubmitted(ILogger logger, string itemId, int version, string jobId);

        [LoggerMessage(12005, LogLevel.Information, "Submitted analysis package rerun {ItemId} version {Version} as job {JobId}")]
        public static partial void AnalysisPackageRerunSubmitted(ILogger logger, string itemId, int version, string jobId);
    }
}
