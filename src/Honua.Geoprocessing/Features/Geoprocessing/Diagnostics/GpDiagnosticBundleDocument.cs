// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Nodes;

namespace Honua.Geoprocessing.Diagnostics;

/// <summary>
/// A diagnostic-bundle v1 envelope plus an optional
/// <see cref="GpDiagnosticEvidence"/> on <see cref="EvidencePropertyName"/>.
/// Bundles written before the attachment still load: the property is absent and
/// <see cref="Evidence"/> is null. <see cref="SchemaVersion"/> stays <c>1.0</c>.
/// </summary>
internal sealed class GpDiagnosticBundleDocument
{
    /// <summary>Envelope contract this reader accepts. Additive GP evidence does not bump it.</summary>
    public const string SchemaVersionValue = "1.0";

    /// <summary>Optional property that carries <see cref="GpDiagnosticEvidence"/>.</summary>
    public const string EvidencePropertyName = "gpEvidence";

    private const int MaxDocumentChars = 48 * 1024 * 1024;

    private static readonly HashSet<string> Classifications = new(StringComparer.Ordinal)
    {
        "unknown", "public", "internal", "customer-data", "secret-suspected"
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true
    };

    private readonly JsonObject _envelope;

    private GpDiagnosticBundleDocument(JsonObject envelope, GpDiagnosticEvidence? evidence)
    {
        _envelope = envelope;
        Evidence = evidence;
        SchemaVersion = ReadString(envelope, "schemaVersion") ?? string.Empty;
    }

    public string SchemaVersion { get; }

    public GpDiagnosticEvidence? Evidence { get; }

    /// <summary>Loads a diagnostic-bundle v1 document. Unknown envelope properties are kept.</summary>
    public static GpDiagnosticBundleDocument Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json.Length == 0 || json.Length > MaxDocumentChars)
        {
            throw new GpDiagnosticBundleException("Diagnostic bundle is empty or larger than the reader allows.");
        }

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64
            });
        }
        catch (JsonException ex)
        {
            throw new GpDiagnosticBundleException("Diagnostic bundle is not valid JSON: " + ex.Message);
        }

        if (parsed is not JsonObject envelope)
        {
            throw new GpDiagnosticBundleException("Diagnostic bundle must be a JSON object.");
        }

        ValidateEnvelope(envelope);
        var evidence = ReadEvidence(envelope);
        envelope.Remove(EvidencePropertyName);
        return new GpDiagnosticBundleDocument(envelope, evidence);
    }

    /// <summary>
    /// Returns a document with the same envelope and a sanitized GP attachment.
    /// Does not mutate this document. Refuses an input export that has no authorization.
    /// </summary>
    public GpDiagnosticBundleDocument WithEvidence(GpDiagnosticEvidenceDraft draft, GpDiagnosticInputExportRequest? export)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return new GpDiagnosticBundleDocument(_envelope, GpDiagnosticEvidenceBuilder.Build(draft, export));
    }

    public string ToJson()
    {
        var clone = (JsonObject)_envelope.DeepClone();
        if (Evidence is not null)
        {
            clone[EvidencePropertyName] = JsonSerializer.SerializeToNode(
                    Evidence,
                    GpDiagnosticBundleJsonContext.Default.GpDiagnosticEvidence)
                ?? throw new GpDiagnosticBundleException("GP evidence could not be written.");
        }

        return clone.ToJsonString(WriteOptions);
    }

    private static GpDiagnosticEvidence? ReadEvidence(JsonObject envelope)
    {
        if (!envelope.TryGetPropertyValue(EvidencePropertyName, out var node) || node is null)
        {
            return null;
        }

        if (node is not JsonObject)
        {
            throw new GpDiagnosticBundleException("GP evidence attachment must be a JSON object.");
        }

        GpDiagnosticEvidence? evidence;
        try
        {
            evidence = node.Deserialize(GpDiagnosticBundleJsonContext.Default.GpDiagnosticEvidence);
        }
        catch (JsonException ex)
        {
            throw new GpDiagnosticBundleException("GP evidence attachment could not be read: " + ex.Message);
        }

        if (evidence is null)
        {
            throw new GpDiagnosticBundleException("GP evidence attachment could not be read.");
        }

        return GpDiagnosticEvidenceBuilder.Accept(evidence);
    }

    private static void ValidateEnvelope(JsonObject envelope)
    {
        var version = ReadString(envelope, "schemaVersion");
        if (!string.Equals(version, SchemaVersionValue, StringComparison.Ordinal))
        {
            throw new GpDiagnosticBundleException("Diagnostic bundle schemaVersion must be 1.0.");
        }

        var classification = ReadString(envelope, "contentClassification");
        if (classification is null || !Classifications.Contains(classification))
        {
            throw new GpDiagnosticBundleException("Diagnostic bundle contentClassification is not supported.");
        }

        if (envelope["consent"] is not JsonObject consent)
        {
            throw new GpDiagnosticBundleException("Diagnostic bundle consent is missing.");
        }

        _ = ReadBool(consent, "redactionAcknowledged");
        _ = ReadBool(consent, "shareWithSupport");
        if (consent.TryGetPropertyValue("grantedBy", out var grantedBy) && grantedBy is not null && ReadJsonString(grantedBy) is null)
        {
            throw new GpDiagnosticBundleException("Diagnostic bundle consent.grantedBy must be a string.");
        }

        if (envelope["envelopes"] is not JsonArray envelopes || envelopes.Count is < 1 or > 50)
        {
            throw new GpDiagnosticBundleException("Diagnostic bundle requires between 1 and 50 envelopes.");
        }

        foreach (var item in envelopes)
        {
            if (item is not JsonObject exchange
                || string.IsNullOrWhiteSpace(ReadString(exchange, "method"))
                || string.IsNullOrWhiteSpace(ReadString(exchange, "normalizedPath")))
            {
                throw new GpDiagnosticBundleException("A diagnostic envelope needs a method and normalizedPath.");
            }
        }
    }

    private static string? ReadString(JsonObject obj, string name)
    {
        if (!obj.TryGetPropertyValue(name, out var node) || node is null)
        {
            return null;
        }

        return ReadJsonString(node);
    }

    private static string? ReadJsonString(JsonNode node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static bool ReadBool(JsonObject obj, string name)
    {
        if (!obj.TryGetPropertyValue(name, out var node) || node is not JsonValue value || !value.TryGetValue<bool>(out var flag))
        {
            throw new GpDiagnosticBundleException("Diagnostic bundle consent." + name + " must be a boolean.");
        }

        return flag;
    }
}
