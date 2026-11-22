// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Runtime.CompilerServices;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing.Execution;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Geoprocessing.Execution;

public sealed partial class LayerSourcedExecutorTests
{
    [UnitTest]
    public async Task Dissolve_SourceSelectionRejected_IsTerminalRefusalWithGuidance()
    {
        const string guidance = "the where clause is not supported; use simple comparisons joined by AND";

        var (status, uri, _) = await RunAsync(
            new LayerDissolveExecutor(ScopeFactory(new RejectingSelectionSource(guidance)), Options(),
                NullLogger<LayerDissolveExecutor>.Instance),
            LayerDissolveExecutor.HandledProcessId,
            ("layerId", "9"));

        status.Should().Be(ExecutionJobStatus.Failed);
        uri.Should().BeNull();
        _lastErrorForAssertions.Should().Contain($"Invalid {LayerDissolveExecutor.HandledProcessId} inputs: {guidance}")
            .And.NotContain("Exception", "a caller error must not collapse to an exception type name");
        _lastRetryableForAssertions.Should().BeFalse("a rejected selector fails the same way on every attempt");
    }

    private sealed class RejectingSelectionSource(string message) : IDagFeatureSource
    {
        public string SourceId => HonuaLayerSourceId;

        public async IAsyncEnumerable<DagSourceFeature> ReadAsync(
            DagSourceRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            throw new DagSourceSelectionException(message);
#pragma warning disable CS0162 // Unreachable: the yield makes this an async iterator.
            yield break;
#pragma warning restore CS0162
        }
    }
}
