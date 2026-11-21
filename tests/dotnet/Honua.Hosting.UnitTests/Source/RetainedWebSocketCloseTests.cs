// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net.WebSockets;
using Honua.Infrastructure.Authentication;
using Honua.TestKit.Attributes;

namespace Honua.Hosting.UnitTests;

public sealed class RetainedWebSocketCloseTests
{
    [UnitTheory]
    [InlineData(WebSocketCloseStatus.PolicyViolation)]
    [InlineData(WebSocketCloseStatus.NormalClosure)]
    public async Task ClientAndPolicyClose_Overlap_EmitOneTerminalFrame(WebSocketCloseStatus firstStatus)
    {
        var policyFirst = firstStatus == WebSocketCloseStatus.PolicyViolation;
        using var inner = new PausedCloseSocket();
        using var socket = new LiveStreamAuthorizationFilter.RetainedWebSocket(inner);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            var first = policyFirst ? socket.EndAuthorizationAsync()
                : socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "finished", timeout.Token);
            await inner.CloseStarted.Task.WaitAsync(timeout.Token);
            var second = policyFirst
                ? socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "finished", timeout.Token)
                : socket.EndAuthorizationAsync();
            Assert.False(second.IsCompleted, "the second Close must wait for the shared writer");
            Assert.Equal(1, inner.CloseWrites);
            inner.FinishClose.TrySetResult();
            await Task.WhenAll(first, second).WaitAsync(timeout.Token);
            Assert.Equal(1, inner.CloseWrites);
            Assert.Equal(policyFirst ? WebSocketCloseStatus.PolicyViolation : WebSocketCloseStatus.NormalClosure,
                inner.SentStatus);
            Assert.Equal(policyFirst ? "authorization-ended" : "finished", inner.SentDescription);
            Assert.False(inner.WasAborted);
        }
        finally
        {
            socket.Release();
        }
    }

    private sealed class PausedCloseSocket : WebSocket
    {
        public TaskCompletionSource CloseStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FinishClose { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CloseWrites { get; private set; }
        public WebSocketCloseStatus? SentStatus { get; private set; }
        public string? SentDescription { get; private set; }
        public bool WasAborted { get; private set; }
        private WebSocketState _state = WebSocketState.CloseReceived;
        public override WebSocketState State => _state;
        public override WebSocketCloseStatus? CloseStatus => WebSocketCloseStatus.NormalClosure;
        public override string? CloseStatusDescription => "finished";
        public override string? SubProtocol => null;
        public override void Abort() { WasAborted = true; _state = WebSocketState.Aborted; }
        public override void Dispose() { }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public override async Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            CloseWrites++;
            SentStatus = closeStatus;
            SentDescription = statusDescription;
            CloseStarted.TrySetResult();
            await FinishClose.Task.WaitAsync(cancellationToken);
            _state = WebSocketState.Closed;
        }
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
