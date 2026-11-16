// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net.WebSockets;
using Honua.Core.Features.Realtime;
using Honua.Protocols.SensorThings.Streaming;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.SensorThings;

public sealed class ObservationStreamWebSocketCloseTests
{
    [UnitTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ClientClose_DuringBlockedWrite_FinishesOrAbortsWithinDeadline(bool writeCompletes)
    {
        var provider = Substitute.For<IRealtimeFanoutProvider>();
        provider.Subscribe(Arg.Any<RealtimeSubscriptionRequest>()).Returns(Substitute.For<IRealtimeSubscription>());
        await using var manager = new ObservationStreamSessionManager(
            NullLogger<ObservationStreamSessionManager>.Instance, provider);
        await using var session = manager.TryCreateSession(new("WebSocket", null, new("tenant-a", null))
        { PrincipalId = "alice" }, out _)!;
        using var socket = new BlockedWriteSocket();
        var feature = Substitute.For<IHttpWebSocketFeature>();
        feature.IsWebSocketRequest.Returns(true);
        feature.AcceptAsync(Arg.Any<WebSocketAcceptContext>()).Returns(socket);
        var context = new DefaultHttpContext();
        context.Features.Set(feature);

        var handling = ObservationStreamEndpoints.HandleWebSocketAsync(context, session, null);
        await socket.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        socket.PeerClose.TrySetResult();
        await socket.CloseRead.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // Keep the write blocked after the receiver observes Close, including its cancellation continuation.
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        Assert.False(socket.WriteToken.IsCancellationRequested, "peer Close must not cancel an active transport write");
        if (writeCompletes)
        {
            socket.FinishWrite.TrySetResult();
        }
        await handling.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(!writeCompletes, socket.WasAborted);
        Assert.Equal(writeCompletes ? 1 : 0, socket.CloseWrites);
        if (writeCompletes)
        {
            Assert.Equal(WebSocketCloseStatus.NormalClosure, socket.AcknowledgedStatus);
            Assert.Equal("finished", socket.AcknowledgedDescription);
        }
    }

    private sealed class BlockedWriteSocket : WebSocket
    {
        public TaskCompletionSource WriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FinishWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource PeerClose { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CloseRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken WriteToken { get; private set; }
        public bool WasAborted { get; private set; }
        public int CloseWrites { get; private set; }
        public WebSocketCloseStatus? AcknowledgedStatus { get; private set; }
        public string? AcknowledgedDescription { get; private set; }
        private WebSocketState _state = WebSocketState.Open;
        public override WebSocketState State => _state;
        public override WebSocketCloseStatus? CloseStatus => WebSocketCloseStatus.NormalClosure;
        public override string? CloseStatusDescription => "finished";
        public override string? SubProtocol => null;
        public override void Abort() { WasAborted = true; _state = WebSocketState.Aborted; }
        public override void Dispose() { }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            Assert.True(FinishWrite.Task.IsCompleted, "Close must not overlap the pending data write");
            CloseWrites++;
            AcknowledgedStatus = closeStatus;
            AcknowledgedDescription = statusDescription;
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            await PeerClose.Task.WaitAsync(cancellationToken);
            _state = WebSocketState.CloseReceived;
            CloseRead.TrySetResult();
            return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true, CloseStatus, CloseStatusDescription);
        }
        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            WriteToken = cancellationToken;
            WriteStarted.TrySetResult();
            try
            {
                await FinishWrite.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Abort();
                throw;
            }
        }
    }
}
