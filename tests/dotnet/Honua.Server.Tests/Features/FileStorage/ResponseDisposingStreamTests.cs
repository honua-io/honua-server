// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.FileStorage;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.FileStorage;

[Collection("Unit")]
public sealed class ResponseDisposingStreamTests
{
    [UnitTest]
    public void Dispose_DisposesInnerStreamAndResponseOnce()
    {
        var inner = new TrackingMemoryStream();
        var response = new TrackingDisposable();
        var stream = new ResponseDisposingStream(inner, response);

        stream.Dispose();
        stream.Dispose();

        inner.DisposeCount.Should().Be(1);
        response.DisposeCount.Should().Be(1);
    }

    [UnitTest]
    public async Task DisposeAsync_UsesAsyncResponseDisposalOnce()
    {
        var inner = new TrackingMemoryStream();
        var response = new TrackingAsyncDisposable();
        var stream = new ResponseDisposingStream(inner, response);

        await stream.DisposeAsync();
        await stream.DisposeAsync();

        inner.DisposeAsyncCount.Should().Be(1);
        response.DisposeAsyncCount.Should().Be(1);
        response.DisposeCount.Should().Be(0);
    }

    private sealed class TrackingMemoryStream : MemoryStream
    {
        public int DisposeCount { get; private set; }
        public int DisposeAsyncCount { get; private set; }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposeCount++;
            }

            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync()
        {
            DisposeAsyncCount++;
            return base.DisposeAsync();
        }
    }

    private sealed class TrackingDisposable : IDisposable
    {
        public int DisposeCount { get; private set; }

        public void Dispose()
        {
            DisposeCount++;
        }
    }

    private sealed class TrackingAsyncDisposable : IDisposable, IAsyncDisposable
    {
        public int DisposeCount { get; private set; }
        public int DisposeAsyncCount { get; private set; }

        public void Dispose()
        {
            DisposeCount++;
        }

        public ValueTask DisposeAsync()
        {
            DisposeAsyncCount++;
            return ValueTask.CompletedTask;
        }
    }
}
