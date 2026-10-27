// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Ai.StudioAiProxy.Domain;
using Honua.Core.Features.Security.Admission;

namespace Honua.Ai.StudioAiProxy.Abstractions;

/// <summary>Internal built-in HTTP preparation capability; the public/custom adapter contract stays unchanged.</summary>
internal interface IStudioAiPreparedHttpAdapter
{
    StudioAiHttpInvocation Prepare(StudioAiProxyProviderOptions options, StudioAiChatRequest request);
    IAsyncEnumerable<StudioAiChatEvent> StreamPreparedAsync(StudioAiHttpInvocation invocation,
        IAiExecutionPermit permit, CancellationToken cancellationToken);
}
