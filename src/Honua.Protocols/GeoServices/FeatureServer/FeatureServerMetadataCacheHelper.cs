// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using Honua.Core.Configuration;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.Helpers;

namespace Honua.Protocols.GeoServices.FeatureServer;

internal sealed class ServiceMetadataCacheKeyInput
{
    internal required MetadataV2GraphSnapshot Snapshot { get; init; }
    internal required MetadataV2Service Service { get; init; }
    internal required IReadOnlyList<(MetadataV2Layer Publication, MetadataV2Object Resource)> Publications { get; init; }
    internal required QueryLimits Limits { get; init; }
    internal required bool SupportsGeobufOutput { get; init; }
    internal required bool SupportsAttachmentUploads { get; init; }
    internal required bool BranchVersioningEnabled { get; init; }
    internal required bool VersionManagementEnabled { get; init; }
    internal required bool OfflineSyncEnabled { get; init; }
    internal required string VersionManagementServerUrl { get; init; }
}

internal sealed class LayerMetadataCacheKeyInput
{
    internal required MetadataV2GraphSnapshot Snapshot { get; init; }
    internal required MetadataV2Service Service { get; init; }
    internal required MetadataV2Layer Publication { get; init; }
    internal required MetadataV2Object SchemaResource { get; init; }
    internal required QueryLimits Limits { get; init; }
    internal required bool SupportsGeobufOutput { get; init; }
    internal required bool SupportsAttachmentUploads { get; init; }
    internal required bool BranchVersioningEnabled { get; init; }
    internal required bool OfflineSyncEnabled { get; init; }
    internal required IReadOnlyList<string> AccessibleRelatedResourceIds { get; init; }
}

internal static class FeatureServerMetadataCacheHelper
{
    internal static string BuildServiceMetadataKey(ServiceMetadataCacheKeyInput input)
    {
        var sb = PooledTextBuilder.Rent();
        try
        {
            var flags = (input.SupportsGeobufOutput ? 1 : 0)
                | (input.SupportsAttachmentUploads ? 2 : 0)
                | (input.BranchVersioningEnabled ? 4 : 0)
                | (input.VersionManagementEnabled ? 8 : 0)
                | (input.OfflineSyncEnabled ? 16 : 0);

            sb.Append("fs-svc|").Append(input.Snapshot.Etag)
                .Append('|').Append(input.Service.Metadata.Id)
                .Append(':').Append(input.Service.Metadata.Generation.GetValueOrDefault())
                .Append('|').Append(input.Limits.MaxRecordCount)
                .Append('|').Append(input.Limits.DefaultRecordCount)
                .Append('|').Append(flags.ToString(CultureInfo.InvariantCulture))
                .Append('|').Append(input.VersionManagementServerUrl);

            foreach (var (publication, resource) in input.Publications)
            {
                sb.Append('|').Append(publication.Metadata.Id)
                    .Append(':').Append(publication.Metadata.Generation.GetValueOrDefault())
                    .Append(':').Append(resource.Metadata.Id)
                    .Append(':').Append(resource.Metadata.Generation.GetValueOrDefault());
                AppendHiddenFields(sb, resource);
            }

            return MetadataDocumentByteCache.HashKey(sb);
        }
        finally
        {
            PooledTextBuilder.Return(sb);
        }
    }

    internal static string BuildLayerMetadataKey(LayerMetadataCacheKeyInput input)
    {
        var sb = PooledTextBuilder.Rent();
        try
        {
            var flags = (input.SupportsGeobufOutput ? 1 : 0)
                | (input.SupportsAttachmentUploads ? 2 : 0)
                | (input.BranchVersioningEnabled ? 4 : 0)
                | (input.OfflineSyncEnabled ? 16 : 0);

            sb.Append("fs-layer|").Append(input.Snapshot.Etag)
                .Append('|').Append(input.Service.Metadata.Id)
                .Append(':').Append(input.Service.Metadata.Generation.GetValueOrDefault())
                .Append('|').Append(input.Publication.Metadata.Id)
                .Append(':').Append(input.Publication.Metadata.Generation.GetValueOrDefault())
                .Append('|').Append(input.SchemaResource.Metadata.Id)
                .Append(':').Append(input.SchemaResource.Metadata.Generation.GetValueOrDefault())
                .Append('|').Append(input.Limits.MaxRecordCount)
                .Append('|').Append(input.Limits.DefaultRecordCount)
                .Append('|').Append(flags.ToString(CultureInfo.InvariantCulture));

            AppendHiddenFields(sb, input.SchemaResource);
            foreach (var relatedId in input.AccessibleRelatedResourceIds)
            {
                sb.Append("|rel:").Append(relatedId);
            }

            return MetadataDocumentByteCache.HashKey(sb);
        }
        finally
        {
            PooledTextBuilder.Return(sb);
        }
    }

    private static void AppendHiddenFields(StringBuilder sb, MetadataV2Object resource)
    {
        var hidden = resource.HiddenSchemaFieldNames();
        if (hidden.Count == 0)
        {
            return;
        }

        var sorted = new string[hidden.Count];
        var index = 0;
        foreach (var fieldName in hidden)
        {
            sorted[index++] = fieldName;
        }

        Array.Sort(sorted, StringComparer.OrdinalIgnoreCase);
        sb.Append("|h:");
        for (var i = 0; i < sorted.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append(sorted[i]);
        }
    }
}
