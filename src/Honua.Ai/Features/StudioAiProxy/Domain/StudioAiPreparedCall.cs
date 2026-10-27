// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Security.Admission;
using Honua.Ai.StudioAiProxy.Abstractions;

namespace Honua.Ai.StudioAiProxy.Domain;

/// <summary>A server-prepared chat request bound to its trusted identity and one charged invocation permit.</summary>
public sealed class StudioAiPreparedCall : IAsyncDisposable
{
    private int _claimed;
    internal StudioAiPreparedCall(StudioAiChatRequest request, AiExecutionContext context, IAiExecutionPermit permit,
        string? providerName = null, IStudioAiPreparedHttpAdapter? httpAdapter = null, StudioAiHttpInvocation? httpInvocation = null)
    {
        Request = request;
        Context = context;
        Permit = permit;
        ProviderName = providerName;
        HttpAdapter = httpAdapter;
        HttpInvocation = httpInvocation;
    }
    internal StudioAiChatRequest Request { get; }
    internal AiExecutionContext Context { get; }
    internal IAiExecutionPermit Permit { get; }
    internal string? ProviderName { get; }
    internal IStudioAiPreparedHttpAdapter? HttpAdapter { get; }
    internal StudioAiHttpInvocation? HttpInvocation { get; }
    internal bool TryClaim() => Interlocked.CompareExchange(ref _claimed, 1, 0) == 0;
    /// <summary>Releases the reservation independently of caller cancellation; the attempt remains charged.</summary>
    public ValueTask DisposeAsync() => Permit.DisposeAsync();
}
