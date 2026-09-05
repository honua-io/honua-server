// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.WorkflowPackages.Domain;

namespace Honua.Server.Features.Operations;

/// <summary>Projects the accepted request into the sealed, reviewer-visible plan.</summary>
internal static class AdminOperationReview
{
    public static OperationProposalPlan Create(IOperationDescriptor descriptor, OperationRequest request,
        OperationPolicyContext context, string method, string path, ProposalRiskLevel risk, string payload)
    {
        var lines = new List<string>
        {
            $"Request: {method} /api/v1/admin{path}",
            $"Tenant: {DisplayText(context.TenantId)}",
        };
        if (request.ConnectionId is not null) lines.Add($"connectionId: {DisplayText(request.ConnectionId)}");
        if (request.ServiceName is not null) lines.Add($"serviceName: {DisplayText(request.ServiceName)}");
        foreach (var field in request.Fields) lines.Add($"field: {DisplayText(field)}");
        foreach (var parameter in request.Parameters.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            var schema = descriptor.InputSchema.FirstOrDefault(input => input.Name == parameter.Key)?.Schema;
            lines.Add($"{DisplayText(parameter.Key)}: {DisplayValue(parameter.Key, parameter.Value, schema)}");
        }

        return new OperationProposalPlan
        {
            Summary = $"{descriptor.Title} ({descriptor.OperationId}).",
            Diff = lines,
            RiskLevel = risk,
            Warnings = ["Credentials, opaque bodies, and undeclared values are redacted from this review. The sealed plan includes the complete accepted request."],
            ExecutionPayload = payload,
        };
    }

    private static string DisplayValue(string name, string? value, WorkflowSchemaDefinition? schema)
    {
        if (schema is null || IsSensitive(name, schema)) return "[redacted]";
        if (value is null) return "null";
        if (schema.Type == WorkflowSchemaValueType.Text)
        {
            if (name.Contains("url", StringComparison.OrdinalIgnoreCase) || schema.Format == "uri")
            {
                if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return "[redacted]";
                value = new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty, Query = string.Empty, Fragment = string.Empty }.Uri.AbsoluteUri;
            }
            return DisplayText(value);
        }
        try
        {
            using var document = JsonDocument.Parse(value);
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
                WriteValue(writer, document.RootElement, schema);
            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }
        catch (JsonException)
        {
            return "[redacted: invalid JSON]";
        }
    }

    private static void WriteValue(Utf8JsonWriter writer, JsonElement value, WorkflowSchemaDefinition schema)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject())
            {
                writer.WritePropertyName(property.Name);
                if (!schema.Properties.TryGetValue(property.Name, out var child) || IsSensitive(property.Name, child))
                    writer.WriteStringValue("[redacted]");
                else
                    WriteValue(writer, property.Value, child);
            }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in value.EnumerateArray())
            {
                if (schema.Items is { } child) WriteValue(writer, item, child);
                else writer.WriteStringValue("[redacted]");
            }
            writer.WriteEndArray();
        }
        else
        {
            value.WriteTo(writer);
        }
    }

    private static bool IsSensitive(string name, WorkflowSchemaDefinition schema) =>
        !name.Equals("secretReference", StringComparison.OrdinalIgnoreCase) &&
        !name.Equals("secretType", StringComparison.OrdinalIgnoreCase) &&
        (schema.Format is "password" or "binary" ||
        name.Equals("body", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("file", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("password", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("token", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("connectionString", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("apiKey", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("privateKey", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("authorization", StringComparison.OrdinalIgnoreCase));

    private static string DisplayText(string? value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        // This is text inside a JSON response field, never an HTML fragment. Keep
        // SQL operators/quotes readable; the outer response performs JSON escaping.
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            writer.WriteStringValue(value);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
