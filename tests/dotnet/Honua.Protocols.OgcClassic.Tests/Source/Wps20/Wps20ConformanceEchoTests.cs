// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Protocols.Ogc.Classic.Wps20;

public sealed class Wps20ConformanceEchoTests
{
    [Fact]
    public async Task ResolveInputAsync_SlowBodyAfterHeaders_ThrowsTranslatedTimeout()
    {
        var options = Substitute.For<IOptionsMonitor<Wps20Options>>();
        options.CurrentValue.Returns(new Wps20Options
        {
            EnableConformanceEcho = true,
            ConformanceReferenceAllowedHosts = ["example.test"]
        });
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns("Test");
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new SlowBodyStream())
        };
        using var echo = new Wps20ConformanceEcho(
            options,
            environment,
            static (_, _) => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") }),
            _ => new StaticResponseHandler(response),
            TimeSpan.FromMilliseconds(100));

        var action = () => echo.ResolveInputAsync(
            new EchoInput("literalInput", EchoValueKind.Reference, "https://example.test/value", "text/plain"),
            CancellationToken.None);

        var exception = await action.Should().ThrowAsync<Wps20EchoException>();
        exception.Which.Message.Should().Be("Reference input retrieval timed out.");
    }

    [Fact]
    public async Task ConnectPinnedAsync_CancellationDisposesCurrentSocket()
    {
        Socket? socket = null;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var action = async () => await Wps20ConformanceEcho.ConnectPinnedAsync(
            [IPAddress.Parse("203.0.113.1")],
            443,
            cancellation.Token,
            family => socket = new Socket(family, SocketType.Stream, ProtocolType.Tcp));

        await action.Should().ThrowAsync<OperationCanceledException>();
        socket.Should().NotBeNull();
        socket!.SafeHandle.IsClosed.Should().BeTrue();
    }

    private sealed class StaticResponseHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(response);
    }

    private sealed class SlowBodyStream : Stream
    {
        private int _readCount;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            ReadCoreAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ReadCoreAsync(buffer, cancellationToken);

        private async ValueTask<int> ReadCoreAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _readCount) == 1)
            {
                buffer.Span[0] = (byte)'x';
                return 1;
            }
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
