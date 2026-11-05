// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Ai.StudioAiProxy.Domain;
using Honua.Core.Features.Security.Admission;

namespace Honua.Ai.StudioAiProxy.Abstractions;

public interface IStudioAiProxyAdapter
{
    string Kind { get; }

    bool IsConfigured(string providerName, StudioAiProxyProviderOptions options);

    // The stream must end with exactly one terminal event, never both done and error and never neither.
    IAsyncEnumerable<StudioAiChatEvent> StreamAsync(
        StudioAiProxyProviderOptions options,
        StudioAiChatRequest request,
        IAiExecutionPermit permit,
        CancellationToken cancellationToken);
}
