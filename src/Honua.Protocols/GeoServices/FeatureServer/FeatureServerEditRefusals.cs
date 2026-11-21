// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.ReadOnlyProviders;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Models;

namespace Honua.Protocols.GeoServices.FeatureServer;

// Edits to a publication served from a source table are refused before any target features are read:
// the edit pre-reads use the managed store, so they would report a visible row as missing, or a
// where-clause edit as a successful edit of nothing.
internal static class FeatureServerEditRefusals
{
    // The GeoServices 405 every FeatureServer edit kind returns for such a publication, otherwise null.
    // Judged per publication, like the advertised capabilities: an edit through this publication would
    // never be read back through it, even when the same storage is also published over managed storage.
    internal static IResult? ForSourceTable(HttpContext context, MetadataV2GraphSnapshot snapshot, MetadataV2Layer publication)
        => ManagedWriteStorage.SourceTableOf(snapshot, publication) is null
            ? null
            : StandardErrorHelpers.CreateFromException(context, new ReadOnlyFeatureWriteException(ManagedWriteStorage.WriteRefusal));
}
