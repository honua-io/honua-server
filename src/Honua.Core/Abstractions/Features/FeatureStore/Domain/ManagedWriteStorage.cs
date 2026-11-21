// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.FeatureStore.Domain;

// Whether the managed feature writer can serve a publication's storage. A publication bound to a
// source table reads that table, so a managed write would never be read back; such a publication is
// neither writable nor advertised as editable.
public static class ManagedWriteStorage
{
    // Returned to callers on some surfaces, so it never names the table, which is internal.
    public const string WriteRefusal =
        "This layer is published over a source table, which this server cannot write through. " +
        "Edit the source table directly and refresh the layer, or republish the data into the managed feature store.";

    private static readonly HashSet<string> _editCapabilities = new(StringComparer.OrdinalIgnoreCase)
    {
        MetadataV2EditCapabilities.Create,
        MetadataV2EditCapabilities.Update,
        MetadataV2EditCapabilities.Delete,
        MetadataV2EditCapabilities.Editing
    };

    // The source table behind the publication when the managed writer cannot serve it, otherwise null.
    public static string? SourceTableOf(MetadataV2GraphSnapshot snapshot, MetadataV2Layer publication)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(publication);

        var binding = snapshot.ResolveStorageBinding(publication);
        if (binding is not { StorageType: MetadataV2StorageType.RelationalTable }
            || !snapshot.Index.ResourcesById.TryGetValue(binding.ResourceId, out var resource))
        {
            return null;
        }

        try
        {
            var mapping = FeatureStorageMapping.FromMetadata(resource, binding);
            return mapping.SupportsManagedWrites ? null : mapping.QualifiedName;
        }
        catch (InvalidOperationException)
        {
            // An unmappable binding is a metadata defect the read paths report; it is not a source table.
            return null;
        }
    }

    // The publication as editing clients should see it: unchanged when the managed writer serves its
    // storage, otherwise with every edit capability removed.
    public static MetadataV2Layer WithoutUnservableEdits(
        MetadataV2GraphSnapshot snapshot, MetadataV2Service service, MetadataV2Layer publication)
    {
        if (SourceTableOf(snapshot, publication) is null)
        {
            return publication;
        }

        var readOnly = MetadataV2EditCapabilities.Resolve(service, publication)
            .Where(static capability => !_editCapabilities.Contains(capability))
            .ToArray();
        return publication with { Capabilities = readOnly.Length == 0 ? ["Query"] : readOnly };
    }
}
