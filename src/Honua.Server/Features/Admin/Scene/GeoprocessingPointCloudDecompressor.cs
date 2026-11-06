// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

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

internal sealed partial class GeoprocessingPointCloudDecompressor : IPointCloudDecompressor
{
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
        var decoded = package.Artifacts
            .Select(artifact => TryDecodeLasDataUri(artifact.Uri, out var bytes) ? bytes : null)
            .FirstOrDefault(bytes => bytes is not null);
        if (decoded is not null)
        {
            return decoded;
        }

        throw new PointCloudDecompressionException(
            "Point-cloud decompression job produced no uncompressed-LAS artifact.");
    }

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

    public IPointCloudDecompressor Create(ClaimsPrincipal principal)
        => new GeoprocessingPointCloudDecompressor(_jobService, principal, _options, _logger, _timeProvider);
}

internal sealed class PointCloudDecompressionOptions
{
    public const string SectionName = "Scene:PointCloud:Decompression";

    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

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
