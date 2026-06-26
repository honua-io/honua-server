// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Security.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NetTopologySuite.Features;
using NetTopologySuite.IO;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace Honua.Geoprocessing.Execution;

/// <summary>
/// Production <see cref="IJobExecutor"/> for the first-class remote DAG source
/// connectors (<c>source.honua-layer</c>, <c>source.esri-featureserver</c>,
/// <c>source.ogc-features</c>, <c>source.wfs</c>, <c>source.postgis</c>). A single
/// executor type is instantiated once per source id; it resolves the matching
/// <see cref="IDagFeatureSource"/> (which REUSES the existing import readers'
/// pagination/streaming) at execution time through an <see cref="IServiceScopeFactory"/>
/// scope — the same provider-resolution pattern <c>import.dataset</c> and
/// <c>sink.external-postgis</c> use, because the source readers live in the active
/// data-provider assembly, not in Honua.Geoprocessing.
///
/// <para>
/// The executor builds the upstream <see cref="DagSourceRequest"/> from the durable
/// step inputs, resolves auth (ArcGIS portal token / PostGIS secure connection) the
/// same way the import path and <c>sink.external-postgis</c> do, streams the matching
/// features, rehydrates each into a NetTopologySuite feature, and publishes the
/// canonical FeatureCollection artifact downstream transform/sink nodes consume.
/// </para>
///
/// <para>
/// The optional <c>since</c> watermark + <c>watermarkField</c> are passed straight
/// through to the reader; persisting the next watermark is owned by the
/// incremental-extract orchestration, not this executor.
/// </para>
/// </summary>
internal sealed partial class RemoteSourceExecutor : IProcessExecutor
{
    private readonly string _processId;
    private readonly IReadOnlySet<string> _processIds;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly IOptionsMonitor<GeoprocessingExecutorOptions> _options;
    private readonly ILogger<RemoteSourceExecutor> _logger;

    private RemoteSourceExecutor(
        string processId,
        IServiceScopeFactory serviceScopeFactory,
        IOptionsMonitor<GeoprocessingExecutorOptions> options,
        ILogger<RemoteSourceExecutor> logger)
    {
        _processId = processId;
        _processIds = new HashSet<string>(StringComparer.Ordinal) { processId };
        _serviceScopeFactory = serviceScopeFactory;
        _options = options;
        _logger = logger;
    }

    /// <summary>The source process id this instance handles.</summary>
    public string HandledProcessId => _processId;

    /// <inheritdoc />
    public IReadOnlySet<string> ProcessIds => _processIds;

    public ExecutionJobKind Kind => ExecutionJobKind.Geoprocessing;

    /// <summary>Creates an executor bound to a single source process id.</summary>
    public static RemoteSourceExecutor ForProcess(
        string processId,
        IServiceScopeFactory serviceScopeFactory,
        IOptionsMonitor<GeoprocessingExecutorOptions> options,
        ILogger<RemoteSourceExecutor> logger)
        => new(processId, serviceScopeFactory, options, logger);

    public async Task<JobExecutionResult> ExecuteAsync(
        ExecutionJobRecord job,
        IJobExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(context);

        var resolved = GeoprocessingDispatchHelper.ResolveProcessId(job.Spec.Parameters);
        if (!string.Equals(resolved, _processId, StringComparison.Ordinal))
        {
            return JobExecutionResult.Failed(
                $"Process id '{resolved ?? "<none>"}' is not handled by the {_processId} executor.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(5, $"Resolving {_processId} source", cancellationToken).ConfigureAwait(false);

        var inputs = new StepInputReader(job.Spec.Parameters);

        using var scope = _serviceScopeFactory.CreateScope();
        var services = scope.ServiceProvider;

        var source = ResolveSource(services);
        if (source is null)
        {
            return JobExecutionResult.Failed(
                $"{_processId} failed: no source connector is registered for the active provider.");
        }

        DagSourceRequest request;
        try
        {
            request = await BuildRequestAsync(inputs, services, cancellationToken).ConfigureAwait(false);
        }
        catch (TransformInputException ex)
        {
            Log.InvalidInputs(_logger, job.OperationId, _processId, ex.PublicMessage);
            return JobExecutionResult.Failed($"Invalid {_processId} inputs: {ex.PublicMessage}");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(20, $"Streaming features from {_processId}", cancellationToken).ConfigureAwait(false);

        var geoJsonReader = new GeoJsonReader();
        var features = new List<IFeature>();
        var maxBytes = _options.CurrentValue.MaxArtifactBytes;

        try
        {
            await foreach (var sourceFeature in source.ReadAsync(request, cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                features.Add(ToNtsFeature(sourceFeature, geoJsonReader));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.SourceReadFailed(_logger, job.OperationId, _processId, ex);
            return JobExecutionResult.Failed($"{_processId} failed during read: {ex.GetType().Name}.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(80, "Encoding source artifact", cancellationToken).ConfigureAwait(false);

        var payload = FeatureCollectionArtifact.WriteFeatureCollection(features, _processId);
        if (payload.Length > maxBytes)
        {
            return JobExecutionResult.Failed(
                $"{_processId} artifact size {payload.Length} bytes exceeds configured MaxArtifactBytes={maxBytes}.");
        }

        var artifactUri = FeatureCollectionArtifact.BuildDataUri(payload);

        cancellationToken.ThrowIfCancellationRequested();
        await context.PublishArtifactAsync(artifactUri, cancellationToken).ConfigureAwait(false);
        await context.ReportProgressAsync(100, $"{_processId} completed ({features.Count} features)", cancellationToken)
            .ConfigureAwait(false);

        return JobExecutionResult.Succeeded();
    }

    private IDagFeatureSource? ResolveSource(IServiceProvider services)
    {
        foreach (var candidate in services.GetServices<IDagFeatureSource>())
        {
            if (string.Equals(candidate.SourceId, _processId, StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        return null;
    }

    private async Task<DagSourceRequest> BuildRequestAsync(
        StepInputReader inputs,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var request = new DagSourceRequest
        {
            Where = inputs.TryGet("where", out var where) ? where : null,
            Bbox = inputs.TryGet("bbox", out var bbox) ? bbox : null,
            OutFields = inputs.TryGet("outFields", out var outFields) ? outFields : null,
            OutputSrid = TryGetInt(inputs, "outSrid"),
            PageSize = TryGetInt(inputs, "pageSize") ?? 1000,
            MaxPages = TryGetInt(inputs, "maxPages"),
            TimeoutSeconds = TryGetInt(inputs, "timeoutSeconds") ?? 120,
            Since = inputs.TryGet("since", out var since) ? since : null,
            WatermarkField = inputs.TryGet("watermarkField", out var watermarkField) ? watermarkField : null,
            CollectionId = inputs.TryGet("collectionId", out var collectionId)
                ? collectionId
                : inputs.TryGet("typeName", out var typeName) ? typeName : null,
            ServiceUrl = inputs.TryGet("serviceUrl", out var serviceUrl) ? serviceUrl : null,
            LayerId = TryGetInt(inputs, "layerId"),
            EsriLayerId = TryGetInt(inputs, "esriLayerId"),
        };

        return _processId switch
        {
            "source.esri-featureserver" => request with
            {
                EsriCredentials = await ResolveEsriCredentialsAsync(inputs, services, cancellationToken).ConfigureAwait(false)
            },
            "source.ogc-features" or "source.wfs" => request with
            {
                Username = inputs.TryGet("username", out var user) ? user : null,
                Password = await ResolveSecretOrInlineAsync(inputs, services, "password", "passwordSecretReference", cancellationToken)
                    .ConfigureAwait(false)
            },
            "source.postgis" => await EnrichPostgisAsync(request, inputs, services, cancellationToken).ConfigureAwait(false),
            _ => request
        };
    }

    private static async Task<DagSourceRequest> EnrichPostgisAsync(
        DagSourceRequest request,
        StepInputReader inputs,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var connectionString = await ResolveSecureConnectionAsync(inputs, services, cancellationToken).ConfigureAwait(false);
        return request with
        {
            PostgisConnectionString = connectionString,
            PostgisSchema = inputs.TryGet("schema", out var schema) ? schema : "public",
            PostgisTable = inputs.TryGet("table", out var table) ? table : null,
            PostgisGeometryColumn = inputs.TryGet("geometryColumn", out var geom) ? geom : "geom",
        };
    }

    private static async Task<GeoservicesCredentialDescriptor?> ResolveEsriCredentialsAsync(
        StepInputReader inputs,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var token = await ResolveSecretOrInlineAsync(inputs, services, "token", "tokenSecretReference", cancellationToken)
            .ConfigureAwait(false);
        var username = inputs.TryGet("username", out var user) ? user : null;
        var password = await ResolveSecretOrInlineAsync(inputs, services, "password", "passwordSecretReference", cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(token) && string.IsNullOrWhiteSpace(username))
        {
            return null;
        }

        return new GeoservicesCredentialDescriptor
        {
            Mode = !string.IsNullOrWhiteSpace(token)
                ? GeoservicesAuthenticationModes.Token
                : GeoservicesAuthenticationModes.Basic,
            AccessToken = token,
            Username = username,
            Password = password
        };
    }

    private static async Task<string?> ResolveSecretOrInlineAsync(
        StepInputReader inputs,
        IServiceProvider services,
        string inlineKey,
        string secretRefKey,
        CancellationToken cancellationToken)
    {
        if (inputs.TryGet(secretRefKey, out var secretReference) && !string.IsNullOrWhiteSpace(secretReference))
        {
            var resolver = services.GetService<IConnectionSecretResolver>();
            if (resolver is null)
            {
                throw new TransformInputException("a secret reference was supplied but no secret resolver is configured.");
            }

            return await resolver.ResolveSecretAsync(secretReference!, cancellationToken).ConfigureAwait(false);
        }

        return inputs.TryGet(inlineKey, out var inline) ? inline : null;
    }

    private static async Task<string?> ResolveSecureConnectionAsync(
        StepInputReader inputs,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var hasName = inputs.TryGet("connectionName", out var connectionName);
        var hasId = inputs.TryGet("connectionId", out var connectionIdText);
        if (hasName == hasId)
        {
            throw new TransformInputException("exactly one of connectionName or connectionId is required.");
        }

        var resolver = services.GetService<ISecureConnectionResolver>();
        if (resolver is null)
        {
            throw new TransformInputException("secure connection resolver is not configured.");
        }

        try
        {
            string connectionString;
            if (hasId)
            {
                if (!Guid.TryParse(connectionIdText, out var connectionId))
                {
                    throw new TransformInputException("connectionId must be a valid GUID.");
                }

                connectionString = await resolver.ResolveConnectionStringAsync(connectionId, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                connectionString = await resolver.ResolveConnectionStringAsync(connectionName!, cancellationToken).ConfigureAwait(false);
            }

            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new TransformInputException("secure connection could not be resolved.");
            }

            return connectionString;
        }
        catch (TransformInputException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            throw new TransformInputException("secure connection could not be resolved.");
        }
    }

    private static Feature ToNtsFeature(DagSourceFeature sourceFeature, GeoJsonReader reader)
    {
        NtsGeometry? geometry = null;
        if (!string.IsNullOrWhiteSpace(sourceFeature.GeometryGeoJson))
        {
            try
            {
                geometry = reader.Read<NtsGeometry>(sourceFeature.GeometryGeoJson);
            }
            catch (Exception ex) when (ex is Newtonsoft.Json.JsonException or ArgumentException or FormatException)
            {
                geometry = null;
            }
        }

        var attributes = new AttributesTable();
        foreach (var (key, value) in sourceFeature.Attributes)
        {
            if (!attributes.Exists(key))
            {
                attributes.Add(key, value);
            }
        }

        return new Feature(geometry, attributes);
    }

    private static int? TryGetInt(StepInputReader inputs, string name)
        => inputs.TryGet(name, out var raw)
            && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static partial class Log
    {
        [LoggerMessage(9280, LogLevel.Warning,
            "Remote source executor rejected job {OperationId} for {ProcessId}: {Reason}")]
        public static partial void InvalidInputs(ILogger logger, string operationId, string processId, string reason);

        [LoggerMessage(9281, LogLevel.Error,
            "Remote source executor failed job {OperationId} during {ProcessId} read")]
        public static partial void SourceReadFailed(ILogger logger, string operationId, string processId, Exception exception);
    }
}
