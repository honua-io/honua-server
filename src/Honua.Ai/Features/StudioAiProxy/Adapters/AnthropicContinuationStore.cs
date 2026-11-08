// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using Honua.Ai.StudioAiProxy.Domain;
using Honua.Core.Features.Security.Admission;

namespace Honua.Ai.StudioAiProxy.Adapters;

internal sealed class AnthropicContinuationStore(TimeProvider clock)
{
    internal const int MaximumTurnCharacters = 262_144;
    private const int MaximumStoredCharacters = 4_194_304;
    private const int MaximumTurns = 128;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private int _storedCharacters;

    internal JsonElement? Restore(string? key, bool required = true)
    {
        if (key is null) return null;
        lock (_lock)
        {
            if (!_entries.TryGetValue(key, out var entry) || entry.ExpiresAt <= clock.GetUtcNow())
            {
                if (!required) return null;
                throw new InvalidOperationException("The provider continuation is unavailable; start a new conversation.");
            }
            using var document = JsonDocument.Parse(entry.Content);
            return document.RootElement.Clone();
        }
    }

    internal void Save(string? key, JsonElement content)
    {
        if (key is null) return;
        var serialized = content.GetRawText();
        if (serialized.Length > MaximumTurnCharacters) return;
        lock (_lock)
        {
            var now = clock.GetUtcNow();
            foreach (var expired in _entries.Where(pair => pair.Value.ExpiresAt <= now).Select(pair => pair.Key).ToArray())
                Remove(expired);
            Remove(key);
            while (_entries.Count >= MaximumTurns || _storedCharacters + serialized.Length > MaximumStoredCharacters)
                Remove(_entries.MinBy(pair => pair.Value.ExpiresAt).Key);
            _entries.Add(key, new Entry(serialized, now.AddMinutes(15)));
            _storedCharacters += serialized.Length;
        }
    }

    internal static string? Key(StudioAiProxyProviderOptions options, StudioAiChatRequest request, string model,
        AiExecutionContext? identity, int assistantIndex, StudioAiMessage assistant)
    {
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
                WriteCanonical(writer, JsonSerializer.SerializeToElement(tool, StudioAiProxyJsonContext.Default.StudioAiToolDefinition));
            writer.WriteStartArray();
            foreach (var message in request.Messages.Take(assistantIndex).Append(assistant))
                WriteCanonical(writer, JsonSerializer.SerializeToElement(message, StudioAiProxyJsonContext.Default.StudioAiMessage));
            writer.WriteEndArray();
            writer.WriteEndArray();
        }
        return Convert.ToHexStringLower(SHA256.HashData(buffer.WrittenSpan));
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(property.Name);
                WriteCanonical(writer, property.Value);
            }
            writer.WriteEndObject();
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in element.EnumerateArray()) WriteCanonical(writer, item);
            writer.WriteEndArray();
        }
        else element.WriteTo(writer);
    }

    private void Remove(string key)
    {
        if (_entries.Remove(key, out var entry)) _storedCharacters -= entry.Content.Length;
    }

    private sealed record Entry(string Content, DateTimeOffset ExpiresAt);
}
