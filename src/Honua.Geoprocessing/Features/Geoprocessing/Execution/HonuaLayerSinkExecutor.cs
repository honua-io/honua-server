// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.RegularExpressions;
using Honua.Core.Features.Authorization;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Validation;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NetTopologySuite.IO;

namespace Honua.Geoprocessing.Execution;

/// <summary>
/// <c>sink.honua-layer</c> executor. Loads the input FeatureCollection into a named layer
/// in the Honua <em>catalog</em> database via the optional <see cref="IHonuaLayerSink"/>
/// capability (replace/append/upsert load modes plus the reserved
/// <c>__pipeline_batch_id</c> rollback tag). The capability is the seam that keeps the
/// geoprocessing dispatcher decoupled from the catalog <c>NpgsqlDataSource</c>: this
/// executor depends only on the abstraction, never on a database type.
/// </summary>
/// <remarks>
/// In a lean, Postgres-free deployment the capability is not registered, so
/// <see cref="_sink"/> resolves to <see langword="null"/> and the node fails closed with a
/// clear "unavailable in this deployment" message — rather than forcing the dispatcher to
/// take a Postgres dependency or leaking catalog credentials through plan parameters.
/// </remarks>
internal sealed partial class HonuaLayerSinkExecutor : IProcessExecutor
{
    internal const string HandledProcessId = "sink.honua-layer";

    private readonly IOptionsMonitor<GeoprocessingExecutorOptions> _options;
    private readonly IHonuaLayerSink? _sink;
    private readonly IServiceScopeFactory? _serviceScopeFactory;
    private readonly ILogger<HonuaLayerSinkExecutor> _logger;

    public HonuaLayerSinkExecutor(
        IOptionsMonitor<GeoprocessingExecutorOptions> options,
        ILogger<HonuaLayerSinkExecutor> logger,
        IHonuaLayerSink? sink = null,
        IServiceScopeFactory? serviceScopeFactory = null)
    {
        _options = options;
        _logger = logger;
        _serviceScopeFactory = serviceScopeFactory;
        _sink = sink;
    }

    /// <inheritdoc />
    public IReadOnlySet<string> ProcessIds { get; } =
        new HashSet<string>(StringComparer.Ordinal) { HandledProcessId };

    /// <inheritdoc />
    public ExecutionJobKind Kind => ExecutionJobKind.Geoprocessing;

    /// <inheritdoc />
    public async Task<JobExecutionResult> ExecuteAsync(
        ExecutionJobRecord job,
        IJobExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(context);

        var resolved = GeoprocessingDispatchHelper.ResolveProcessId(job.Spec.Parameters);
        if (!string.Equals(resolved, HandledProcessId, StringComparison.Ordinal))
        {
            return JobExecutionResult.Failed(
                $"Process id '{resolved ?? "<none>"}' is not handled by the {HandledProcessId} executor.");
        }

        // Fail closed in lean deployments: the catalog-layer capability is registered only
        // when the catalog database is present. Surface a clear, actionable message instead
        // of a generic provider error.
        if (_sink is null)
        {
            Log.SinkUnavailable(_logger, job.OperationId);
            return JobExecutionResult.Failed(
                $"The {HandledProcessId} sink is unavailable in this deployment: it requires a Honua catalog " +
                "database, which is not configured here. Use sink.external-postgis or a file sink instead.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(10, "Parsing honua-layer sink inputs", cancellationToken).ConfigureAwait(false);

        var inputs = new StepInputReader(job.Spec.Parameters);
        if (!inputs.TryGetRequired("input", out var inputUri, out var inputError))
        {
            return JobExecutionResult.Failed($"Invalid {HandledProcessId} inputs: {inputError}");
        }

        if (!FeatureCollectionArtifact.TryParseDataUri(inputUri, out var source, out var parseError, _options.CurrentValue.MaxArtifactBytes))
        {
            return JobExecutionResult.Failed($"Invalid {HandledProcessId} inputs: 'input' {parseError}");
        }

        string schema;
        string table;
        string geometryColumn;
        int targetSrid;
        HonuaLayerLoadMode loadMode;
        List<string> keyFields;
        try
        {
            schema = Identifier(inputs.GetOrDefault("schema", "public"));
            table = Identifier(inputs.Require("layer"));
            geometryColumn = Identifier(inputs.GetOrDefault("geometryColumn", "geom"));
            targetSrid = RequireSrid(inputs, "targetSrid");
            loadMode = ParseLoadMode(inputs.GetOrDefault("loadMode", "append"));
            keyFields = ParseKeyFields(inputs.GetOrDefault("keyFields", string.Empty));

            if (loadMode == HonuaLayerLoadMode.Upsert && keyFields.Count == 0)
            {
                throw new TransformInputException("loadMode 'upsert' requires a non-empty 'keyFields' list.");
            }
        }
        catch (TransformInputException ex)
        {
            return JobExecutionResult.Failed($"Invalid {HandledProcessId} inputs: {ex.PublicMessage}");
        }

        var batchId = inputs.GetOrDefault("batchId", job.OperationId);

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(20, "Authorizing sink destination", cancellationToken).ConfigureAwait(false);

        string? denial;
        try
        {
            denial = await AuthorizeDestinationAsync(schema, table, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Fail closed on an unexpected authorization-pipeline failure rather than let it
            // propagate as a crashed worker or (worse) fall through to an unauthorized load.
            Log.DestinationAuthorizationFailed(_logger, job.OperationId, ex);
            return JobExecutionResult.Failed($"{HandledProcessId} destination authorization failed: {ex.GetType().Name}.");
        }

        if (denial is not null)
        {
            Log.DestinationAuthorizationDenied(_logger, job.OperationId, schema, table);
            return JobExecutionResult.Failed($"{HandledProcessId} destination denied: {denial}");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(40, "Encoding features", cancellationToken).ConfigureAwait(false);

        var wkbWriter = new WKBWriter();
        var rows = new List<HonuaLayerSinkRow>();
        long rejected = 0;
        foreach (var feature in source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (feature.Geometry is null)
            {
                rejected++;
                continue;
            }

            rows.Add(new HonuaLayerSinkRow(
                wkbWriter.Write(feature.Geometry),
                SinkFeatureEncoder.BuildAttributesJson(feature, batchId)));
        }

        await context.ReportProgressAsync(70, "Loading into catalog layer", cancellationToken).ConfigureAwait(false);

        HonuaLayerSinkOutcome outcome;
        try
        {
            outcome = await _sink.LoadAsync(
                new HonuaLayerSinkRequest(schema, table, geometryColumn, targetSrid, loadMode, batchId, keyFields),
                rows,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Intentionally broad: this is the job's top-level sink-load boundary — any
            // failure must become a Failed job result (not a crashed worker), and the full
            // exception is logged while only the exception type name reaches the result.
            Log.SinkLoadFailed(_logger, job.OperationId, ex);
            // The sink load is transactional, so a failure left the catalog table unchanged.
            return JobExecutionResult.Failed($"{HandledProcessId} load failed: {ex.GetType().Name}.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await context.PublishArtifactAsync(
            SinkResultArtifact.Build(
                HandledProcessId,
                ("schema", outcome.Schema),
                ("layer", outcome.Table),
                ("loadMode", loadMode.ToString()),
                ("batchId", outcome.BatchId),
                ("featuresWritten", outcome.FeaturesWritten),
                ("featuresRejected", rejected)),
            cancellationToken).ConfigureAwait(false);
        await context.ReportProgressAsync(100, $"{HandledProcessId} completed", cancellationToken).ConfigureAwait(false);

        return JobExecutionResult.Succeeded();
    }

    /// <summary>
    /// Authorizes the resolved <c>schema.table</c> destination before any DDL/DML runs
    /// (#4625). Generic <c>Process.Execute</c> permission (the only gate this node had) says
    /// nothing about whether the submitter may write to THIS destination — <c>sink.honua-layer</c>
    /// accepted arbitrary caller-supplied schema/table text and PostgresHonuaLayerSink wrote
    /// through it with only SQL-injection-shaped identifier validation, no target authorization.
    /// </summary>
    /// <returns>
    /// <see langword="null"/> when the destination is authorized; otherwise a safe,
    /// no-provider-detail denial reason for the job-failure message.
    /// </returns>
    private async Task<string?> AuthorizeDestinationAsync(
        string schema, string table, CancellationToken cancellationToken)
    {
        if (_serviceScopeFactory is null)
        {
            return "the destination could not be authorized: no authorization scope is available in this deployment.";
        }

        // Mirrors source.honua-layer's fail-closed contract (honua-server#3068): a job with no
        // captured submitter cannot be constrained to anyone, so it must be refused rather than
        // writing under an unconstrained identity.
        var submitter = JobSecurityScope.Current?.Submitter;
        if (submitter is null)
        {
            return "this job carries no submitter security context, so its sink destination cannot be authorized.";
        }

        using var scope = _serviceScopeFactory.CreateScope();
        var services = scope.ServiceProvider;
        var metadataProvider = services.GetService<IMetadataV2GraphProvider>();
        if (metadataProvider is null)
        {
            return $"{HandledProcessId} requires the catalog metadata provider, which is not configured in this deployment.";
        }

        var principal = JobSecurityContextCapture.Restore(submitter);
        var snapshot = await metadataProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);

        // Resolve existing destinations to a stable catalog resource and authorize against IT,
        // rather than trusting the caller-supplied text: two different (schema, table) pairs can
        // never be treated as the same target, and the SAME pair always resolves to the SAME
        // layer for the lifetime of this check.
        if (FindExistingLayerId(snapshot, schema, table) is { } existingLayerId)
        {
            var evaluationContext = new DefaultHttpContext { RequestServices = services, User = principal };
            var validation = await LayerValidationHelpers.ValidateLayerWriteAccessV2Async(
                    evaluationContext,
                    existingLayerId,
                    LayerValidationHelpers.ValidationProtocol.OgcFeatures,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return validation.IsValid
                ? null
                : "you do not have write permission on the catalog layer bound to this destination.";
        }

        // No existing catalog layer resolves to this destination: this is a request to CREATE a
        // brand-new table. Ordinary per-layer write grants say nothing about that authority — an
        // editor of layer A must not be able to conjure layer B into existence in any schema the
        // Postgres role can reach — so creation is governed separately and requires an
        // administrative role (#4625 AC: "Govern creation of new destinations separately").
        var rbacOptions = services.GetService<IOptions<RbacOptions>>()?.Value ?? new RbacOptions();
        return RbacRoleClaims.IsAdmin(principal, rbacOptions, services)
            ? null
            : $"creating a new {HandledProcessId} destination requires an administrative role; target an " +
              "existing authorized catalog layer instead, or have an administrator create it first.";
    }

    /// <summary>
    /// Finds the storage-layer id of the relational catalog layer whose resolved schema/table
    /// matches the requested destination, or <see langword="null"/> when no existing layer
    /// resolves there (a brand-new destination).
    /// </summary>
    internal static int? FindExistingLayerId(MetadataV2GraphSnapshot snapshot, string schema, string table)
    {
        foreach (var binding in snapshot.Graph.StorageBindings)
        {
            if (binding.StorageType != MetadataV2StorageType.RelationalTable || binding.StorageLayerId is not { } layerId)
            {
                continue;
            }

            if (!snapshot.Index.ResourcesById.TryGetValue(binding.ResourceId, out var resource))
            {
                continue;
            }

            FeatureStorageMapping mapping;
            try
            {
                mapping = FeatureStorageMapping.FromMetadata(resource, binding);
            }
            catch (InvalidOperationException)
            {
                // Not usable as relational feature storage (validation failure inside
                // FromMetadata) — cannot be the destination's existing layer.
                continue;
            }

            var mappedSchema = string.IsNullOrEmpty(mapping.SchemaName) ? "public" : mapping.SchemaName;
            if (string.Equals(mappedSchema, schema, StringComparison.Ordinal)
                && string.Equals(mapping.TableName, table, StringComparison.Ordinal))
            {
                return layerId;
            }
        }

        return null;
    }

    private static HonuaLayerLoadMode ParseLoadMode(string raw) => raw.Trim().ToLowerInvariant() switch
    {
        "append" => HonuaLayerLoadMode.Append,
        "replace" => HonuaLayerLoadMode.Replace,
        "upsert" => HonuaLayerLoadMode.Upsert,
        _ => throw new TransformInputException($"loadMode '{raw}' is invalid; expected append, replace, or upsert.")
    };

    private static List<string> ParseKeyFields(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var keys = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            // Key fields are interpolated into JSONB accessors in the sink; validate them.
            keys.Add(Identifier(part));
        }

        return keys;
    }

    private static string Identifier(string value)
    {
        if (!IdentifierRegex().IsMatch(value))
        {
            throw new TransformInputException(
                $"identifier '{value}' is invalid; identifiers must match ^[A-Za-z_][A-Za-z0-9_]*$ " +
                "(they cannot be parameterized in DDL/DML).");
        }

        return value;
    }

    private static int RequireSrid(StepInputReader inputs, string key)
    {
        if (!inputs.TryGet(key, out var raw)
            || !int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            || value <= 0)
        {
            throw new TransformInputException($"requires a positive integer '{key}' option.");
        }

        return value;
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex IdentifierRegex();

    private static partial class Log
    {
        [LoggerMessage(9270, LogLevel.Warning,
            "honua-layer sink refused job {OperationId}: catalog sink capability is not registered in this deployment")]
        public static partial void SinkUnavailable(ILogger logger, string operationId);

        [LoggerMessage(9271, LogLevel.Error,
            "honua-layer sink failed job {OperationId} during catalog load")]
        public static partial void SinkLoadFailed(ILogger logger, string operationId, Exception exception);

        [LoggerMessage(9272, LogLevel.Warning,
            "honua-layer sink refused job {OperationId}: destination '{Schema}.{Table}' was not authorized for the submitting principal")]
        public static partial void DestinationAuthorizationDenied(ILogger logger, string operationId, string schema, string table);

        [LoggerMessage(9273, LogLevel.Error,
            "honua-layer sink failed job {OperationId} while authorizing the sink destination")]
        public static partial void DestinationAuthorizationFailed(ILogger logger, string operationId, Exception exception);
    }
}
