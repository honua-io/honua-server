// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.ControlPlane;

/// <summary>
/// Unit tests for <see cref="ProcessExecutorRouteTable"/> — the shared
/// auto-registration routing-table builder used by the managed and GDAL
/// geoprocessing dispatchers (GP Devkit authoring contract #2122). Pins the
/// fail-fast diagnostics that protect the authoring contract from a duplicate or
/// empty process-id declaration.
/// </summary>
public sealed class ProcessExecutorRouteTableTests
{
    private sealed class FakeProcessExecutor(params string[] ids) : IProcessExecutor
    {
        public IReadOnlySet<string> ProcessIds { get; } =
            new HashSet<string>(ids, StringComparer.Ordinal);

        public ExecutionJobKind Kind => ExecutionJobKind.Geoprocessing;

        public Task<JobExecutionResult> ExecuteAsync(
            ExecutionJobRecord job,
            IJobExecutionContext context,
            CancellationToken cancellationToken)
            => Task.FromResult(JobExecutionResult.Succeeded());
    }

    [UnitTest]
    public void Build_MapsEachDeclaredIdToItsExecutor()
    {
        var a = new FakeProcessExecutor("geometry.buffer");
        var b = new FakeProcessExecutor("surface.slope", "surface.aspect");

        var table = ProcessExecutorRouteTable.Build(new IProcessExecutor[] { a, b });

        table.Should().HaveCount(3);
        table["geometry.buffer"].Should().BeSameAs(a);
        table["surface.slope"].Should().BeSameAs(b);
        table["surface.aspect"].Should().BeSameAs(b);
    }

    [UnitTest]
    public void Build_DuplicateIdAcrossExecutors_Throws()
    {
        var a = new FakeProcessExecutor("geometry.buffer");
        var b = new FakeProcessExecutor("geometry.buffer");

        var act = () => ProcessExecutorRouteTable.Build(new IProcessExecutor[] { a, b });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*geometry.buffer*");
    }

    [UnitTest]
    public void Build_ExecutorWithNoIds_Throws()
    {
        var empty = new FakeProcessExecutor();

        var act = () => ProcessExecutorRouteTable.Build(new IProcessExecutor[] { empty });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*no process ids*");
    }

    [UnitTest]
    public void Build_ExecutorWithWhitespaceId_Throws()
    {
        var bad = new FakeProcessExecutor("  ");

        var act = () => ProcessExecutorRouteTable.Build(new IProcessExecutor[] { bad });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*whitespace process id*");
    }
}
