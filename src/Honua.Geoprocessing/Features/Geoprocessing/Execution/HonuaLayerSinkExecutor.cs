// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.RegularExpressions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
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
    private readonly ILogger<HonuaLayerSinkExecutor> _logger;

    public HonuaLayerSinkExecutor(
        IOptionsMonitor<GeoprocessingExecutorOptions> options,
        ILogger<HonuaLayerSinkExecutor> logger,
        IHonuaLayerSink? sink = null)
    {
        _options = options;
        _logger = logger;
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
        catch (Exception ex)
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
    }
}
