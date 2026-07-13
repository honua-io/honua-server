// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Security.Claims;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Scene.Abstractions;
using Honua.Geoprocessing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Admin.Scene;

/// <summary>
/// <see cref="IPointCloudDecompressor"/> backed by the canonical geoprocessing job
/// runtime (#1854). It builds a single-step <c>pcloud.translate</c>
/// <see cref="AnalysisPlan"/>, submits it through <see cref="IGeoprocessingJobService"/>
/// (which stamps the native runtime profile so the dispatch routes to the
/// out-of-tree PDAL worker), polls the job to a terminal state, and returns the
/// decompressed uncompressed-LAS bytes the managed scene tiler consumes.
/// </summary>
/// <remarks>
/// <para>
/// This is the worker-boundary crossing the <c>Honua.Scene</c> tiling subsystem
/// deliberately does not take: the scene assembly stays free of any
/// geoprocessing/job dependency, and the server composition root binds this
/// adapter so the admin point-cloud ingest endpoint can decompress LAZ/COPC (or
/// reproject a projected source) before tiling. The actual decompression is the
/// thin-adapter-over-canonical-pipeline path AGENTS.md prescribes — no LAS/LAZ
/// parsing, no 3D-Tiles generation lives here.
/// </para>
/// <para>
/// The decompressed LAS round-trips back through the existing managed
/// <c>LasPointCloudReader</c> + <c>PointCloudTilesetBuilder</c>, so the ingest
/// outcome is identical to a natively-uploaded uncompressed LAS.
/// </para>
/// </remarks>
internal sealed partial class GeoprocessingPointCloudDecompressor : IPointCloudDecompressor
{
    /// <summary>The canonical process id handled by the out-of-tree PDAL worker.</summary>
    public const string ProcessId = "pcloud.translate";

    private const string LasArtifactContentType = "application/vnd.las";
    private const string DataUriPrefix = "data:";

    private readonly IGeoprocessingJobService _jobService;
    private readonly ClaimsPrincipal _principal;
    private readonly IOptions<PointCloudDecompressionOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<GeoprocessingPointCloudDecompressor> _logger;

    public GeoprocessingPointCloudDecompressor(
        IGeoprocessingJobService jobService,
        ClaimsPrincipal principal,
        IOptions<PointCloudDecompressionOptions> options,
        ILogger<GeoprocessingPointCloudDecompressor> logger,
        TimeProvider? timeProvider = null)
    {
        _jobService = jobService ?? throw new ArgumentNullException(nameof(jobService));
        _principal = principal ?? throw new ArgumentNullException(nameof(principal));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<byte[]> DecompressAsync(byte[] source, string? sourceSrs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Length == 0)
        {
            throw new PointCloudDecompressionException("Point-cloud source buffer is empty.");
        }

        var opts = _options.Value;

        var inputs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["source"] = Convert.ToBase64String(source),
        };
        if (!string.IsNullOrWhiteSpace(sourceSrs))
        {
            inputs["sourceSrs"] = sourceSrs.Trim();
        }

        var planId = $"pcloud-translate-{Guid.NewGuid():N}";
        var plan = new AnalysisPlan
        {
            PlanId = planId,
            IntentId = planId,
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "translate",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = ProcessId,
                    Inputs = inputs,
                },
            ],
            Outputs = [ArtifactKind.Raster],
        };

        await _jobService
            .EnsureCallerAuthorizedAsync(_principal, OperatorResourceType.Job, OperatorOperation.Create, cancellationToken)
            .ConfigureAwait(false);

        ExecutionJobRecord job;
        try
        {
            job = await _jobService
                .SubmitJobAsync(plan, idempotencyKey: null, _principal, protocolMetadata: null, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not PointCloudDecompressionException)
        {
            Log.SubmitFailed(_logger, ex);
            throw new PointCloudDecompressionException(
                "Failed to submit the point-cloud decompression job to the geoprocessing runtime.", ex);
        }

        var terminal = await PollToTerminalAsync(job, opts, cancellationToken).ConfigureAwait(false);
        if (terminal.Status != ExecutionJobStatus.Succeeded)
        {
            throw new PointCloudDecompressionException(
                $"Point-cloud decompression job ended with status '{terminal.Status}'.");
        }

        var package = await _jobService
            .GetJobResultsAsync(terminal.OperationId, _principal, cancellationToken)
            .ConfigureAwait(false);

        var las = ExtractLasArtifact(package);
        Log.Completed(_logger, terminal.OperationId, las.Length);
        return las;
    }

    private async Task<ExecutionJobRecord> PollToTerminalAsync(
        ExecutionJobRecord job,
        PointCloudDecompressionOptions opts,
        CancellationToken cancellationToken)
    {
        if (IsTerminal(job.Status))
        {
            return job;
        }

        var deadline = _timeProvider.GetUtcNow() + opts.Timeout;
        var current = job;
        while (!IsTerminal(current.Status))
        {
            if (_timeProvider.GetUtcNow() >= deadline)
            {
                throw new PointCloudDecompressionException(
                    $"Point-cloud decompression job '{current.OperationId}' did not complete within {opts.Timeout}.");
            }

            await Task.Delay(opts.PollInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
            current = await _jobService
                .GetJobAsync(current.OperationId, _principal, cancellationToken)
                .ConfigureAwait(false);
        }

        return current;
    }

    private static byte[] ExtractLasArtifact(AnalysisResultPackage package)
    {
        // Not a simple filter: each artifact's URI must be decoded via TryDecodeLasDataUri,
        // whose out-parameter is the return value, so a LINQ Where/Select would not
        // simplify this short-circuiting "find first decodable artifact" loop.
        foreach (var artifact in package.Artifacts)
        {
            if (TryDecodeLasDataUri(artifact.Uri, out var bytes))
            {
                return bytes;
            }
        }

        throw new PointCloudDecompressionException(
            "Point-cloud decompression job produced no uncompressed-LAS artifact.");
    }

    /// <summary>
    /// Decodes a <c>data:application/vnd.las;base64,&lt;payload&gt;</c> artifact URI
    /// into its LAS bytes. Returns <see langword="false"/> for any other URI shape
    /// so the caller can skip non-LAS artifacts.
    /// </summary>
    internal static bool TryDecodeLasDataUri(string? uri, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrWhiteSpace(uri) || !uri.StartsWith(DataUriPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var commaIndex = uri.IndexOf(',', StringComparison.Ordinal);
        if (commaIndex < 0)
        {
            return false;
        }

        var header = uri.AsSpan(DataUriPrefix.Length, commaIndex - DataUriPrefix.Length);
        if (!header.Contains(LasArtifactContentType, StringComparison.OrdinalIgnoreCase)
            || !header.Contains("base64", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            bytes = Convert.FromBase64String(uri[(commaIndex + 1)..]);
        }
        catch (FormatException)
        {
            return false;
        }

        return bytes.Length != 0;
    }

    private static bool IsTerminal(ExecutionJobStatus status)
        => status is ExecutionJobStatus.Succeeded or ExecutionJobStatus.Failed or ExecutionJobStatus.Cancelled;

    private static partial class Log
    {
        [LoggerMessage(EventId = 8490, Level = LogLevel.Error,
            Message = "Point-cloud decompression job submission failed.")]
        public static partial void SubmitFailed(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 8491, Level = LogLevel.Information,
            Message = "Point-cloud decompression job {OperationId} completed; decompressed LAS is {Bytes} bytes.")]
        public static partial void Completed(ILogger logger, string operationId, int bytes);
    }
}

/// <summary>
/// Composition-root factory that binds the request principal to a
/// <see cref="GeoprocessingPointCloudDecompressor"/> (#1854). The decompressor
/// authorizes and submits under the calling admin operator's identity, which is
/// only available per-request, so the long-lived dependencies are captured here
/// and the principal is supplied when the ingest endpoint creates the
/// per-request instance.
/// </summary>
internal sealed class GeoprocessingPointCloudDecompressorFactory : IPointCloudDecompressorFactory
{
    private readonly IGeoprocessingJobService _jobService;
    private readonly IOptions<PointCloudDecompressionOptions> _options;
    private readonly ILogger<GeoprocessingPointCloudDecompressor> _logger;
    private readonly TimeProvider _timeProvider;

    public GeoprocessingPointCloudDecompressorFactory(
        IGeoprocessingJobService jobService,
        IOptions<PointCloudDecompressionOptions> options,
        ILogger<GeoprocessingPointCloudDecompressor> logger,
        TimeProvider? timeProvider = null)
    {
        _jobService = jobService ?? throw new ArgumentNullException(nameof(jobService));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public IPointCloudDecompressor Create(ClaimsPrincipal principal)
        => new GeoprocessingPointCloudDecompressor(_jobService, principal, _options, _logger, _timeProvider);
}

/// <summary>
/// Tunables for the out-of-process point-cloud decompression dispatch (#1854):
/// how long the ingest request waits for the <c>pcloud.translate</c> worker and
/// how frequently it polls the job for completion.
/// </summary>
internal sealed class PointCloudDecompressionOptions
{
    /// <summary>Configuration section binding key.</summary>
    public const string SectionName = "Scene:PointCloud:Decompression";

    /// <summary>Maximum time the ingest request waits for the worker to finish. Default 5 minutes.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Interval between job-status polls while waiting. Default 1 second.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Parses an invariant timespan/seconds value, falling back to <paramref name="fallback"/>.</summary>
    internal static TimeSpan ParseDuration(string? raw, TimeSpan fallback)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }
        if (TimeSpan.TryParse(raw, CultureInfo.InvariantCulture, out var span) && span > TimeSpan.Zero)
        {
            return span;
        }
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
        {
            return TimeSpan.FromSeconds(seconds);
        }
        return fallback;
    }
}
