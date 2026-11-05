// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Ai.StudioAiProxy.Domain;
using Honua.Core.Features.Security.Admission;

namespace Honua.Ai.StudioAiProxy.Abstractions;

public interface IStudioAiProxyService
{
    bool Enabled { get; }

    Task<StudioAiCapabilitiesResponse> GetCapabilitiesAsync(CancellationToken cancellationToken = default);

    // Validation is separate from streaming because SSE cannot change the HTTP status after the first byte is written.
    string? ValidateRequest(StudioAiChatRequest request);

    Task<StudioAiPreparedCall> PrepareChatAsync(StudioAiChatRequest request, AiExecutionContext? executionContext,
        CancellationToken cancellationToken);

    IAsyncEnumerable<StudioAiChatEvent> StreamPreparedChatAsync(StudioAiPreparedCall prepared,
        StudioAiProxyCallSummary summary, CancellationToken cancellationToken);

    IAsyncEnumerable<StudioAiChatEvent> StreamChatAsync(
        StudioAiChatRequest request,
        StudioAiProxyCallSummary summary,
        CancellationToken cancellationToken);
}

public sealed class StudioAiProxyCallSummary
{
    public string Provider { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    public int? PromptTokens { get; set; }

    public int? CompletionTokens { get; set; }

    public long LatencyMs { get; set; }

    public StudioAiStopReason? StopReason { get; set; }

    public bool Succeeded { get; set; }

    public string? ErrorMessage { get; set; }
}
