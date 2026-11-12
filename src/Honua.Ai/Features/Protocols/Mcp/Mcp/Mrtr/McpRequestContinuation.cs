// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Tools;
using Microsoft.Extensions.Options;

namespace Honua.Ai.Protocols.Mcp.Mrtr;

// A tool that can return input_required names the requestState kinds it resumes. The dispatcher
// refuses a requestState for any other tool or kind before the tool runs.
internal interface IMcpInputRequiredTool
{
    bool Resumes(McpRequestStateKind kind);
}

// What the tool wants the client to ask for. The dispatcher serialises it through
// McpModernResults.InputRequired; a tool never writes resultType itself.
internal sealed record McpInputRequired(IReadOnlyDictionary<string, JsonElement> InputRequests, string RequestState);

// One answer from inputResponses. Content is present only when the person accepted.
internal sealed record McpInputResponse(string Action, JsonElement Content)
{
    public bool Accepted => Action == "accept";
}

// Process-wide MRTR services: the requestState codec (null when no signing secret is configured)
// and the Console approval origin (null when not configured). Both come from configuration, so
// every replica derives the same values.
internal sealed class McpMrtrRuntime
{
    public McpMrtrRuntime(McpRequestStateCodec? codec, Uri? approvalConsoleUrl)
    {
        Codec = codec;
        ApprovalConsoleUrl = approvalConsoleUrl;
    }

    public McpRequestStateCodec? Codec { get; }

    public Uri? ApprovalConsoleUrl { get; }

    public static McpMrtrRuntime Create(IServiceProvider services)
    {
        var options = services.GetRequiredService<IOptions<McpOptions>>().Value;
        var configuration = services.GetService<IConfiguration>();
        var secret = !string.IsNullOrWhiteSpace(options.SessionSigningKey)
            ? options.SessionSigningKey
            : configuration?[McpSessionModeResolver.DefaultConnectionKey];
        var codec = string.IsNullOrWhiteSpace(secret)
            ? null
            : new McpRequestStateCodec(secret, services.GetService<TimeProvider>());
        return new McpMrtrRuntime(codec, ParseConsoleUrl(options.ApprovalConsoleUrl));
    }

    // Only an absolute https origin (http on loopback for development) is accepted; anything else
    // disables URL-mode approval rather than sending a person to an unexpected page.
    internal static Uri? ParseConsoleUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || !(uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
        {
            return null;
        }

        var path = uri.AbsolutePath.EndsWith('/') ? uri.AbsolutePath : uri.AbsolutePath + "/";
        return new UriBuilder(uri) { Path = path }.Uri;
    }

    public Uri? ApprovalUrl(string proposalId) => ApprovalConsoleUrl is null
        ? null
        : new Uri(ApprovalConsoleUrl, "inbox?proposalId=" + Uri.EscapeDataString(proposalId));
}

// The MRTR view of one modern tools/call: declared input kinds and, on a retry, the verified state
// and the client's answers. Absent for legacy sessions and when MRTR is not configured.
internal sealed class McpRequestContinuation
{
    private static readonly object HttpContextItemKey = new();

    private readonly McpRequestStateBinding _binding;
    private readonly JsonElement? _inputResponses;

    private McpRequestContinuation(
        McpRequestStateCodec codec,
        McpRequestStateBinding binding,
        McpToolsCallParams parameters,
        McpRequestState? state)
    {
        Codec = codec;
        _binding = binding;
        Modes = McpElicitationModes.Read(parameters.Meta);
        State = state;
        _inputResponses = parameters.InputResponses;
    }

    public McpElicitationModes Modes { get; }

    public McpRequestState? State { get; }

    public McpRequestStateCodec Codec { get; }

    public static McpRequestContinuation? From(HttpContext httpContext) =>
        httpContext.Items.TryGetValue(HttpContextItemKey, out var value) ? value as McpRequestContinuation : null;

    // Attaches the continuation for this call, or returns the refusal when the client sent a
    // requestState or inputResponses that cannot be honoured. The tool must not run after a refusal.
    internal static McpJsonRpcError? Refusal(HttpContext httpContext, IMcpTool tool, McpToolsCallParams parameters)
    {
        Detach(httpContext);
        var continuing = parameters.RequestState is { ValueKind: not JsonValueKind.Undefined }
            || parameters.InputResponses is { ValueKind: not JsonValueKind.Undefined };
        var runtime = httpContext.RequestServices.GetService<McpMrtrRuntime>();
        var principalKey = McpAuthorizationHelper.ResolveSessionBindingKey(httpContext);
        if (!httpContext.Items.ContainsKey(McpModernProtocol.RequestItemKey)
            || runtime?.Codec is not { } codec
            || principalKey is null or McpSessionManager.AnonymousPrincipalKey)
        {
            // Legacy sessions, anonymous callers (who share one binding) and unconfigured servers have
            // no MRTR; a continuation there is malformed.
            return continuing ? McpErrorMapper.InvalidRequestState() : null;
        }

        var binding = new McpRequestStateBinding(principalKey, tool.Name, parameters.Arguments);
        McpRequestState? state = null;
        if (continuing)
        {
            state = ReadState(codec, binding, tool, parameters);
            if (state is null)
            {
                return McpErrorMapper.InvalidRequestState();
            }
        }

        httpContext.Items[HttpContextItemKey] = new McpRequestContinuation(codec, binding, parameters, state);
        return null;
    }

    internal static void Detach(HttpContext httpContext) => httpContext.Items.Remove(HttpContextItemKey);

    private static McpRequestState? ReadState(
        McpRequestStateCodec codec,
        McpRequestStateBinding binding,
        IMcpTool tool,
        McpToolsCallParams parameters)
    {
        if (parameters.InputResponses is { ValueKind: not JsonValueKind.Object }
            || parameters.RequestState is not { ValueKind: JsonValueKind.String } encoded
            || codec.Read(encoded.GetString(), binding) is not { } state)
        {
            return null;
        }

        return tool is IMcpInputRequiredTool resumable && resumable.Resumes(state.Kind) ? state : null;
    }

    public McpInputResponse? Response(string key)
    {
        if (_inputResponses is not { ValueKind: JsonValueKind.Object } responses
            || !responses.TryGetProperty(key, out var response)
            || response.ValueKind != JsonValueKind.Object
            || McpModernProtocol.ReadString(response, "action") is not { } action
            || action is not ("accept" or "decline" or "cancel"))
        {
            return null;
        }

        var content = response.TryGetProperty("content", out var value) && value.ValueKind == JsonValueKind.Object
            ? value.Clone()
            : default;
        return new McpInputResponse(action, content);
    }

    public McpToolsCallResult InputRequired(string key, JsonElement elicitationParams, McpRequestStateGrant grant)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("method", "elicitation/create");
            writer.WritePropertyName("params");
            elicitationParams.WriteTo(writer);
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.ToArray());
        var requests = new Dictionary<string, JsonElement>(StringComparer.Ordinal) { [key] = document.RootElement.Clone() };
        return new McpToolsCallResult { InputRequired = new McpInputRequired(requests, Codec.Mint(_binding, grant)) };
    }
}

// Elicitation modes the request declared in io.modelcontextprotocol/clientCapabilities.
// An empty elicitation object means form mode only, as in the 2025-11-25 schema.
internal readonly record struct McpElicitationModes(bool Form, bool Url)
{
    public static McpElicitationModes Read(JsonElement? meta)
    {
        if (meta is not { ValueKind: JsonValueKind.Object } value
            || !value.TryGetProperty(McpModernProtocol.CapabilitiesMetadata, out var capabilities)
            || capabilities.ValueKind != JsonValueKind.Object
            || !capabilities.TryGetProperty("elicitation", out var elicitation)
            || elicitation.ValueKind != JsonValueKind.Object)
        {
            return default;
        }

        var form = elicitation.TryGetProperty("form", out var f) && f.ValueKind == JsonValueKind.Object;
        var url = elicitation.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.Object;
        return form || url ? new McpElicitationModes(form, url) : new McpElicitationModes(true, false);
    }
}
