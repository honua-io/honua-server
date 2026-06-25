// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Geoprocessing.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Geoprocessing.Testing.Tests;

/// <summary>
/// Executor builders for the golden-SDK self-tests (GP Devkit P6, issue #2127). The
/// managed <c>geometry.buffer</c> executor proves the geometry (NTS-tolerance) path fully
/// offline; the lightweight <see cref="StubArtifactExecutor"/> publishes a caller-chosen
/// data-URI artifact so the scalar/structural path and the run-failure paths can be driven
/// without any native (GDAL) dependency.
/// </summary>
internal static class TestExecutors
{
    public static GeometryBufferJobExecutor Buffer()
    {
        var monitor = Substitute.For<IOptionsMonitor<GeoprocessingExecutorOptions>>();
        monitor.CurrentValue.Returns(new GeoprocessingExecutorOptions
        {
            MaxArtifactBytes = 50L * 1024L * 1024L,
            ResultRetention = TimeSpan.FromDays(7),
        });
        return new GeometryBufferJobExecutor(monitor, NullLogger<GeometryBufferJobExecutor>.Instance);
    }
}

/// <summary>
/// Minimal <see cref="IProcessExecutor"/> that publishes a fixed data-URI artifact (or
/// fails / publishes nothing), letting the self-tests exercise the SDK's scalar/structural
/// comparison and failure handling with no native runtime.
/// </summary>
internal sealed class StubArtifactExecutor : IProcessExecutor
{
    private readonly string? _artifact;
    private readonly string? _failure;

    private StubArtifactExecutor(string processId, string? artifact, string? failure)
    {
        ProcessIds = new HashSet<string>(StringComparer.Ordinal) { processId };
        _artifact = artifact;
        _failure = failure;
    }

    public IReadOnlySet<string> ProcessIds { get; }

    public ExecutionJobKind Kind => ExecutionJobKind.Geoprocessing;

    /// <summary>Publishes <paramref name="text"/> as a <c>text/csv</c> base64 data URI.</summary>
    public static StubArtifactExecutor Csv(string processId, string text) =>
        new(processId, DataUri("text/csv", text), failure: null);

    /// <summary>Publishes <paramref name="json"/> as an <c>application/json</c> base64 data URI.</summary>
    public static StubArtifactExecutor Json(string processId, string json) =>
        new(processId, DataUri("application/json", json), failure: null);

    /// <summary>Fails terminally with the supplied message before publishing anything.</summary>
    public static StubArtifactExecutor Failing(string processId, string message) =>
        new(processId, artifact: null, failure: message);

    public async Task<JobExecutionResult> ExecuteAsync(
        ExecutionJobRecord job,
        IJobExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (_failure is not null)
        {
            return JobExecutionResult.Failed(_failure);
        }

        if (_artifact is not null)
        {
            await context.PublishArtifactAsync(_artifact, cancellationToken).ConfigureAwait(false);
        }

        return JobExecutionResult.Succeeded();
    }

    private static string DataUri(string mediaType, string text) =>
        $"data:{mediaType};base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
}
