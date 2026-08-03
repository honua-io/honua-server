// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Azure.Storage.Blobs.Models;
using Honua.FileStorage;

namespace Honua.Server.Tests.Features.FileStorage;

public sealed class BlobDownloadStreamTests
{
    [Fact]
    public async Task DisposeAsync_DisposesSdkOwnedContentExactlyOnce()
    {
        var content = new TrackingStream([1, 2, 3]);
        var result = BlobsModelFactory.BlobDownloadStreamingResult(content, details: null!);
        var stream = new BlobDownloadStream(result);

        await stream.DisposeAsync();
        await stream.DisposeAsync();

        Assert.Equal(1, content.DisposeCount);
    }

    private sealed class TrackingStream(byte[] content) : MemoryStream(content, writable: false)
    {
        public int DisposeCount { get; private set; }

        protected override void Dispose(bool disposing)
        {
            if (disposing && DisposeCount == 0)
            {
                DisposeCount++;
            }

            base.Dispose(disposing);
        }
    }
}
