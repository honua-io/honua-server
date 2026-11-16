// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Nodes;

namespace Honua.Core.Features.Publishing.Descriptors;

// The cross-field rules of publication descriptor v1 that JSON Schema cannot express.
// They assume a document the schema accepts.
public static class PublicationDescriptorRules
{
    public const string CaptureId = "capture-id";
    public const string UniqueFieldNames = "unique-field-names";
    public const string PublicFieldsOnly = "public-fields-only";
    public const string IdentityMapping = "identity-mapping";
    public const string BoundsOrder = "bounds-order";
    public const string ZoomOrder = "zoom-order";

    public static readonly IReadOnlyList<string> All =
        [CaptureId, UniqueFieldNames, PublicFieldsOnly, IdentityMapping, BoundsOrder, ZoomOrder];

    public static IReadOnlyList<string> Violations(PublicationDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var node = JsonSerializer.SerializeToNode(descriptor, PublicationDescriptorJsonContext.Default.PublicationDescriptor)!;
        return Violations(node.AsObject());
    }

    public static IReadOnlyList<string> Violations(JsonObject descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var fields = descriptor["fields"]!.AsArray().Select(field => field!.AsObject()).ToList();
        var publicFields = fields
            .Where(field => field["public"]!.GetValue<bool>())
            .Select(field => Text(field, "name"))
            .ToHashSet(StringComparer.Ordinal);
        var artifacts = descriptor["artifacts"]!.AsObject().Select(property => property.Value!.AsObject()).ToList();
        var captureId = Text(descriptor["capture"]!.AsObject(), "captureId");

        var violations = new List<string>();
        AddWhen(violations, CaptureId, artifacts.Any(artifact => Text(artifact, "captureId") != captureId));
        AddWhen(violations, UniqueFieldNames,
            fields.Select(field => Text(field, "name")).Distinct(StringComparer.Ordinal).Count() != fields.Count);
        AddWhen(violations, PublicFieldsOnly, artifacts.SelectMany(ArtifactFields).Any(name => !publicFields.Contains(name)));
        AddWhen(violations, IdentityMapping, !IdentityResolvesInBothArtifacts(descriptor, fields, publicFields));

        var filterBounds = descriptor["capture"]!["filter"]?["bbox"];
        AddWhen(violations, BoundsOrder, IsInverted(descriptor["bounds"]!) || (filterBounds is not null && IsInverted(filterBounds)));

        var pmtiles = descriptor["artifacts"]!["pmtiles"]!;
        AddWhen(violations, ZoomOrder, pmtiles["minzoom"]!.GetValue<int>() > pmtiles["maxzoom"]!.GetValue<int>());
        return violations;
    }

    private static void AddWhen(List<string> violations, string rule, bool violated)
    {
        if (violated)
        {
            violations.Add(rule);
        }
    }

    private static string Text(JsonObject node, string property) => node[property]!.GetValue<string>();

    private static bool IsInverted(JsonNode bounds)
    {
        var values = bounds.AsArray().Select(value => value!.GetValue<double>()).ToArray();
        return values[0] >= values[2] || values[1] >= values[3];
    }

    private static IEnumerable<string> ArtifactFields(JsonObject artifact) =>
        artifact["fields"]!.AsArray().Select(name => name!.GetValue<string>());

    private static bool IdentityResolvesInBothArtifacts(
        JsonObject descriptor,
        List<JsonObject> fields,
        HashSet<string> publicFields)
    {
        var identity = descriptor["identity"]!.AsObject();
        var identityField = Text(identity, "field");
        var declared = fields.FirstOrDefault(field => Text(field, "name") == identityField);
        var artifacts = descriptor["artifacts"]!;
        return declared is not null
            && Text(declared, "type") == "string"
            && publicFields.Contains(identityField)
            && Text(identity, "pmtilesProperty") == identityField
            && Text(identity, "geoparquetColumn") == identityField
            && ArtifactFields(artifacts["pmtiles"]!.AsObject()).Contains(identityField)
            && ArtifactFields(artifacts["geoparquet"]!.AsObject()).Contains(identityField);
    }
}
