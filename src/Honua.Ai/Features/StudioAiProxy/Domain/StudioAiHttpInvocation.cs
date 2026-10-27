// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Ai.StudioAiProxy.Domain;

/// <summary>Private owned provider bytes and original provenance, never a loggable record or financial receipt.</summary>
internal sealed class StudioAiHttpInvocation
{
    private readonly StudioAiProxyProviderOptions _options;
    private readonly byte[] _body;

    internal StudioAiHttpInvocation(StudioAiProxyProviderOptions options, StudioAiChatRequest request, string suffix,
        Func<StudioAiProxyProviderOptions, StudioAiChatRequest, string, byte[]> serialize)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(request);
        _options = SnapshotOptions(options);
        Request = CopyRequest(request);
        Model = string.IsNullOrWhiteSpace(Request.Model) ? _options.Model : Request.Model!;
        // Append exactly as the existing adapters do; resolving a rooted suffix
        // against a base URI would silently discard an operator's base path.
        if (!Uri.TryCreate(_options.Endpoint?.TrimEnd('/') + suffix, UriKind.Absolute, out var destination)
            || (destination.Scheme != Uri.UriSchemeHttp && destination.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(destination.Host) || !string.IsNullOrEmpty(destination.UserInfo)
            || !string.IsNullOrEmpty(destination.Query) || !string.IsNullOrEmpty(destination.Fragment))
            throw new ArgumentException("The provider HTTP invocation destination is invalid.");
        Destination = destination;
        _body = serialize(_options, Request, Model);
    }

    internal StudioAiChatRequest Request { get; }
    internal string Model { get; }
    internal Uri Destination { get; }
    internal byte[] CopyBody() => (byte[])_body.Clone();
    internal StudioAiProxyProviderOptions CopyOptions() => SnapshotOptions(_options);

    internal static StudioAiProxyProviderOptions SnapshotOptions(StudioAiProxyProviderOptions options) => new()
    {
        Kind = options.Kind,
        Endpoint = options.Endpoint,
        Model = options.Model,
        Region = options.Region,
        ApiKey = options.ApiKey,
        ApiKeyFromEnvironment = options.ApiKeyFromEnvironment,
        TimeoutSeconds = options.TimeoutSeconds,
        MaxTokens = options.MaxTokens,
        SupportsTools = options.SupportsTools
    };

    private static JsonElement CopyJson(JsonElement value)
        => value.ValueKind == JsonValueKind.Undefined ? default : value.Clone();

    private static StudioAiChatRequest CopyRequest(StudioAiChatRequest request) => new()
    {
        ExecutionContext = request.ExecutionContext,
        AcceptedRequestJson = request.AcceptedRequestJson is { } accepted ? (byte[])accepted.Clone() : null,
        Certification = request.Certification is { } certification ? new()
        {
            CandidateId = certification.CandidateId,
            TenantId = certification.TenantId,
            ReleaseId = certification.ReleaseId,
            EndpointIdentity = certification.EndpointIdentity,
            ActionId = certification.ActionId,
            RunNonce = certification.RunNonce
        } : null,
        Provider = request.Provider,
        Model = request.Model,
        System = request.System,
        MaxTokens = request.MaxTokens,
        Temperature = request.Temperature,
        ToolChoice = request.ToolChoice is { } choice ? new() { Mode = choice.Mode, ToolName = choice.ToolName } : null,
        Messages = Array.AsReadOnly(request.Messages.Select(message => new StudioAiMessage
        {
            Role = message.Role,
            Content = message.Content,
            ToolCallId = message.ToolCallId,
            ToolName = message.ToolName,
            ToolCalls = message.ToolCalls is { } calls ? Array.AsReadOnly(calls.Select(call => new StudioAiToolCall
            {
                Id = call.Id,
                Name = call.Name,
                Arguments = CopyJson(call.Arguments)
            }).ToArray()) : null
        }).ToArray()),
        Tools = request.Tools is { } tools ? Array.AsReadOnly(tools.Select(tool => new StudioAiToolDefinition
        {
            Name = tool.Name,
            Description = tool.Description,
            InputSchema = CopyJson(tool.InputSchema),
            Annotations = tool.Annotations is { } annotations ? CopyJson(annotations) : null,
            OutputSchema = tool.OutputSchema is { } output ? CopyJson(output) : null
        }).ToArray()) : null
    };
}
