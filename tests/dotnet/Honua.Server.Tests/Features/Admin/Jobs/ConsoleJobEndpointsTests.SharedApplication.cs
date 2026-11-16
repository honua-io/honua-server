// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.TestKit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Admin.Jobs;

public sealed partial class ConsoleJobEndpointsTests
{
    // Cases reset and reseed the in-memory doubles. The Test environment hosts none of the
    // timer-driven job-store pollers, so nothing rewrites a case's seeded jobs mid-request.
    public sealed class SharedApplication : IAsyncLifetime
    {
        internal RetentionTestClock RetentionClock { get; } = new();
        internal InMemoryJobStore JobStore { get; } = new();
        internal InMemoryLogStore LogStore { get; } = new();
        internal RecordingJobQueue JobQueue { get; } = new();
        internal RecordingBatchBackend RemoteBackend { get; } = new("remote", supportsRetry: true);
        internal RecordingBatchBackend RemoteNoRetryBackend { get; } = new("remote-no-retry", supportsRetry: false);
        internal StubArtifactStore ArtifactStore { get; } = new();
        internal WebAppFixture Fixture { get; }

        public SharedApplication()
        {
            Fixture = new WebAppFixture()
                .ConfigureServices(services =>
                {
                    services.RemoveAll<TimeProvider>();
                    services.AddSingleton<TimeProvider>(RetentionClock);
                    services.RemoveAll<IExecutionJobStore>();
                    services.RemoveAll<IExecutionLogStore>();
                    services.RemoveAll<IJobQueue>();
                    services.RemoveAll<IArtifactStore>();
                    services.AddSingleton<IExecutionJobStore>(JobStore);
                    services.AddSingleton<IExecutionLogStore>(LogStore);
                    services.AddSingleton<IJobQueue>(JobQueue);
                    services.AddSingleton<IBatchComputeBackend>(RemoteBackend);
                    services.AddSingleton<IBatchComputeBackend>(RemoteNoRetryBackend);
                    services.AddSingleton<IArtifactStore>(ArtifactStore);
                });
        }

        public Task InitializeAsync() => Fixture.InitializeAsync();

        public Task DisposeAsync() => Fixture.DisposeAsync();
    }
}
