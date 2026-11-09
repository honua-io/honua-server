// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using Honua.Ai.StudioAiProxy.Domain;
using Honua.Core.Features.Security.Admission;

namespace Honua.Ai.StudioAiProxy.Adapters;

internal sealed class AnthropicContinuationStore(TimeProvider clock)
{
    internal const int MaximumTurnCharacters = 262_144;
    internal const string UnavailableMessage = "The provider continuation is unavailable; start a new conversation.";
    private const int MaximumStoredCharacters = 4_194_304;
    private const int MaximumTurns = 128;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private int _storedCharacters;

    internal Turn? Restore(string? key)
        => key is null ? null : TryRestore(key)
            ?? throw new InvalidOperationException(UnavailableMessage);

    internal Turn? TryRestore(string? key)
    {
        if (key is null) return null;
        lock (_lock)
        {
            if (!_entries.TryGetValue(key, out var entry) || entry.ExpiresAt <= clock.GetUtcNow())
            {
                Remove(key);
                return null;
            }
            using var document = JsonDocument.Parse(entry.Content);
            return new Turn(document.RootElement.Clone(), entry.CredentialFingerprint);
        }
    }

    internal void Save(string? key, Turn turn)
    {
        if (key is null) return;
        var serialized = turn.Content.GetRawText();
        if (serialized.Length > MaximumTurnCharacters) return;
        lock (_lock)
        {
            var now = clock.GetUtcNow();
            foreach (var expired in _entries.Where(pair => pair.Value.ExpiresAt <= now).Select(pair => pair.Key).ToArray())
                Remove(expired);
            Remove(key);
            while (_entries.Count >= MaximumTurns || _storedCharacters + serialized.Length > MaximumStoredCharacters)
                Remove(_entries.MinBy(pair => pair.Value.ExpiresAt).Key);
            _entries.Add(key, new Entry(serialized, now.AddMinutes(15), turn.CredentialFingerprint));
            _storedCharacters += serialized.Length;
        }
    }

    internal static string? Key(Scope scope, int assistantIndex, StudioAiMessage assistant)
    {
        var (options, request, model, identity) = scope;
        if (identity is not { IsValid: true }) return null;
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            writer.WriteStringValue(identity.DeploymentId);
            writer.WriteStringValue(identity.ActorKey);
            writer.WriteStringValue(identity.UseCase);
            writer.WriteStringValue(identity.WorkflowKey);
            writer.WriteStringValue(options.Endpoint);
            writer.WriteStringValue(options.ApiKey);
            writer.WriteStringValue(request.Provider);
            writer.WriteStringValue(model);
            writer.WriteStringValue(request.System);
            foreach (var tool in request.Tools ?? [])
                JsonSerializer.Serialize(writer, tool, StudioAiProxyJsonContext.Default.StudioAiToolDefinition);
            writer.WriteStartArray();
            foreach (var message in request.Messages.Take(assistantIndex).Append(assistant))
                JsonSerializer.Serialize(writer, message, StudioAiProxyJsonContext.Default.StudioAiMessage);
            writer.WriteEndArray();
            writer.WriteEndArray();
        }
        return Convert.ToHexStringLower(SHA256.HashData(StudioAiTranscriptSigner.Canonicalize(buffer.WrittenSpan)));
    }

    internal static string CredentialFingerprint(string credential)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(credential)));

    private void Remove(string key)
    {
        if (_entries.Remove(key, out var entry)) _storedCharacters -= entry.Content.Length;
    }

    private sealed record Entry(string Content, DateTimeOffset ExpiresAt, string CredentialFingerprint);

    internal sealed record Turn(JsonElement Content, string CredentialFingerprint);

    internal sealed record Scope(StudioAiProxyProviderOptions Options, StudioAiChatRequest Request,
        string Model, AiExecutionContext? Identity)
    {
        internal HashSet<string> RestoredCredentials { get; } = new(StringComparer.Ordinal);
    }
}
