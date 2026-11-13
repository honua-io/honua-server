// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Newtonsoft.Json.Linq;

namespace Honua.Core.Tests.Features.Publishing.PublicationDescriptor;

// The cross-field rules of publication descriptor v1 that JSON Schema cannot
// express. Run only on documents the schema accepts.
internal static class PublicationDescriptorRules
{
    public const string CaptureId = "capture-id";
    public const string UniqueFieldNames = "unique-field-names";
    public const string PublicFieldsOnly = "public-fields-only";
    public const string IdentityMapping = "identity-mapping";
    public const string BoundsOrder = "bounds-order";
    public const string ZoomOrder = "zoom-order";

    public static readonly IReadOnlyList<string> All =
        [CaptureId, UniqueFieldNames, PublicFieldsOnly, IdentityMapping, BoundsOrder, ZoomOrder];

    public static IReadOnlyList<string> Violations(JObject descriptor)
    {
        var fields = descriptor["fields"]!.Children<JObject>().ToList();
        var publicFields = fields
            .Where(field => field.Value<bool>("public"))
            .Select(field => field.Value<string>("name")!)
            .ToHashSet(StringComparer.Ordinal);
        var artifacts = ((JObject)descriptor["artifacts"]!).Properties()
            .Select(property => (JObject)property.Value)
            .ToList();

        var violations = new List<string>();
        if (artifacts.Any(artifact => artifact.Value<string>("captureId") != descriptor["capture"]!.Value<string>("captureId")))
        {
            violations.Add(CaptureId);
        }

        if (fields.Select(field => field.Value<string>("name")).Distinct(StringComparer.Ordinal).Count() != fields.Count)
        {
            violations.Add(UniqueFieldNames);
        }

        if (artifacts.SelectMany(ArtifactFields).Any(name => !publicFields.Contains(name)))
        {
            violations.Add(PublicFieldsOnly);
        }

        if (!IdentityResolvesInBothArtifacts(descriptor, fields, publicFields))
        {
            violations.Add(IdentityMapping);
        }

        var filterBounds = descriptor["capture"]!["filter"]?["bbox"];
        if (IsInverted(descriptor["bounds"]!) || (filterBounds is not null && IsInverted(filterBounds)))
        {
            violations.Add(BoundsOrder);
        }

        var pmtiles = descriptor["artifacts"]!["pmtiles"]!;
        if (pmtiles.Value<int>("minzoom") > pmtiles.Value<int>("maxzoom"))
        {
            violations.Add(ZoomOrder);
        }

        return violations;
    }

    private static bool IsInverted(JToken bounds)
    {
        var values = bounds.Values<double>().ToArray();
        return values[0] >= values[2] || values[1] >= values[3];
    }

    private static IEnumerable<string> ArtifactFields(JObject artifact) =>
        artifact["fields"]!.Values<string>().Select(name => name!);

    private static bool IdentityResolvesInBothArtifacts(
        JObject descriptor,
        List<JObject> fields,
        HashSet<string> publicFields)
    {
        var identity = descriptor["identity"]!;
        var identityField = identity.Value<string>("field")!;
        var declared = fields.FirstOrDefault(field => field.Value<string>("name") == identityField);
        return declared?.Value<string>("type") == "string"
            && publicFields.Contains(identityField)
            && identity.Value<string>("pmtilesProperty") == identityField
            && identity.Value<string>("geoparquetColumn") == identityField
            && ArtifactFields((JObject)descriptor["artifacts"]!["pmtiles"]!).Contains(identityField)
            && ArtifactFields((JObject)descriptor["artifacts"]!["geoparquet"]!).Contains(identityField);
    }
}
