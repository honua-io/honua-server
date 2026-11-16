// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Geoprocessing.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Geoprocessing.Testing.Tests;

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

    public static StubArtifactExecutor Csv(string processId, string text) =>
        new(processId, DataUri("text/csv", text), failure: null);

    public static StubArtifactExecutor Json(string processId, string json) =>
        new(processId, DataUri("application/json", json), failure: null);

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
