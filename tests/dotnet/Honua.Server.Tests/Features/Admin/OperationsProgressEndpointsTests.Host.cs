// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.TestKit;
using Honua.TestKit.Helpers;

namespace Honua.Server.Tests.Features.Admin;

public sealed partial class OperationsProgressEndpointsTests
{
    // One application serves every case that uses the default durable doubles. Each case
    // writes progress and jobs under its own new operation id and deletes its progress
    // afterwards; the queue is drained before each case so depth assertions stay exact.
    // Cases that need a different job store, queue or remote backend build their own
    // application.
    public sealed class OperationsHost : IAsyncLifetime
    {
        private readonly InMemoryJobQueue _queue = new();

        // Isolate durable operation state (job store and queue) from other test classes.
        public OperationsHost()
        {
            Fixture = new WebAppFixture()
                .ReplaceRequestService<IExecutionJobStore>(new InMemoryExecutionJobStore())
                .ReplaceRequestService<IJobQueue>(_queue);
        }

        public WebAppFixture Fixture { get; }

        public Task InitializeAsync() => Fixture.InitializeAsync();

        public Task DisposeAsync() => Fixture.DisposeAsync();

        public async Task DrainQueueAsync()
        {
            string? claimed;
            do
            {
                claimed = await _queue.TryClaimAsync("operations-progress-test-reset");
            }
            while (claimed is not null);
        }
    }
}
