// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers;
using System.Text;
using System.Text.Json;
using Honua.Ai.StudioAiProxy.Adapters.Models;
using Honua.Ai.StudioAiProxy.Domain;

namespace Honua.Ai.StudioAiProxy.Adapters;

internal sealed class AnthropicAssistantTurn
{
    private readonly List<ContentBlock> _blocks = [];
    private readonly Dictionary<int, ContentBlock> _openBlocks = [];
    private int _characters;

    internal bool CaptureLimitExceeded { get; private set; }

    internal void Capture(AnthropicStreamFrame frame, string payload)
    {
        if (CaptureLimitExceeded) return;
        if (payload.Length > AnthropicContinuationStore.MaximumTurnCharacters - _characters)
        {
            CaptureLimitExceeded = true;
            _blocks.Clear();
            _openBlocks.Clear();
            return;
        }
        _characters += payload.Length;
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        if (frame.Type == "content_block_start" && frame.ContentBlock is not null && frame.Index is { } startIndex)
        {
            var block = new ContentBlock(root.GetProperty("content_block").Clone());
            _blocks.Add(block);
            _openBlocks[startIndex] = block;
        }
        else if (frame.Type == "content_block_delta" && frame.Index is { } deltaIndex
            && _openBlocks.TryGetValue(deltaIndex, out var block))
        {
            var field = frame.Delta?.Type switch
            {
                "thinking_delta" => "thinking",
                "signature_delta" => "signature",
                "text_delta" => "text",
                "input_json_delta" => "partial_json",
                _ => null
            };
            if (field is not null && root.GetProperty("delta").TryGetProperty(field, out var value)
                && value.ValueKind == JsonValueKind.String)
                block.Append(field, value.GetString()!);
        }
        else if (frame.Type == "content_block_stop" && frame.Index is { } stopIndex)
            _openBlocks.Remove(stopIndex);
    }

    internal JsonElement Content()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var block in _blocks) block.WriteTo(writer);
            writer.WriteEndArray();
        }
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    internal static StudioAiMessage VisibleMessage(JsonElement content)
    {
        var text = new StringBuilder();
        List<StudioAiToolCall> calls = [];
        foreach (var block in content.EnumerateArray())
        {
            switch (block.GetProperty("type").GetString())
            {
                case "text":
                    if (block.TryGetProperty("text", out var value)) text.Append(value.GetString());
                    break;
                case "tool_use":
                    calls.Add(new StudioAiToolCall
                    {
                        Id = block.GetProperty("id").GetString()!,
                        Name = block.GetProperty("name").GetString()!,
                        Arguments = block.GetProperty("input").Clone()
                    });
                    break;
            }
        }
        return new StudioAiMessage { Role = StudioAiRole.Assistant, Content = text.ToString(), ToolCalls = calls };
    }

    private sealed class ContentBlock(JsonElement initial)
    {
        private readonly Dictionary<string, StringBuilder> _deltas = new(StringComparer.Ordinal);

        internal void Append(string field, string value)
        {
            if (!_deltas.TryGetValue(field, out var buffer))
            {
                buffer = new StringBuilder();
                if (initial.TryGetProperty(field, out var prefix) && prefix.ValueKind == JsonValueKind.String)
                    buffer.Append(prefix.GetString());
                _deltas.Add(field, buffer);
            }
            buffer.Append(value);
        }

        internal void WriteTo(Utf8JsonWriter writer)
        {
            writer.WriteStartObject();
            foreach (var property in initial.EnumerateObject())
            {
                if (_deltas.ContainsKey(property.Name) || property.Name == "input" && _deltas.ContainsKey("partial_json")) continue;
                property.WriteTo(writer);
            }
            foreach (var (field, value) in _deltas)
            {
                if (field == "partial_json")
                {
                    writer.WritePropertyName("input");
                    using var document = JsonDocument.Parse(value.ToString());
                    document.RootElement.WriteTo(writer);
                }
                else writer.WriteString(field, value.ToString());
            }
            if (initial.GetProperty("type").GetString() == "tool_use"
                && !initial.TryGetProperty("input", out _) && !_deltas.ContainsKey("partial_json"))
            {
                writer.WriteStartObject("input");
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }
    }
}
