// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Server.Features.Admin.Models;

namespace Honua.Server.Features.Admin;

/// <summary>Maps the admin publish body onto the domain request the publisher accepts.</summary>
internal static class LayerPublishRequestMapper
{
    internal static bool TryParseStorageMode(string? value, out LayerStorageMode storageMode)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized) || normalized.Equals("source", StringComparison.OrdinalIgnoreCase))
        {
            storageMode = LayerStorageMode.Source;
            return true;
        }

        if (normalized.Equals("managed", StringComparison.OrdinalIgnoreCase))
        {
            storageMode = LayerStorageMode.Managed;
            return true;
        }

        storageMode = LayerStorageMode.Source;
        return false;
    }

    internal static LayerPublishRequest CreateDomain(PublishLayerRequest request, Guid connectionId, string? tenantId)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!LayerPublicationNamespace.IsValid(request.Namespace))
        {
            throw new LayerPublishingException(
                LayerPublishingErrorKind.Validation,
                "Validation failed: namespace must contain 1-128 ASCII letters, digits, '.', '_' or '-'.");
        }

        if (request.Namespace is not null && string.IsNullOrWhiteSpace(tenantId))
        {
            throw new LayerPublishingException(
                LayerPublishingErrorKind.Validation,
                "Validation failed: namespaced publication requires a resolved tenant.");
        }

        if (!LayerSourceGovernance.TryCreate(
                request.License,
                request.Attribution,
                request.Publisher,
                request.LicenseUrl,
                request.SourceUrl,
                out var sourceGovernance,
                out var governanceError))
        {
            throw new LayerPublishingException(
                LayerPublishingErrorKind.Validation,
                $"Validation failed: {governanceError}");
        }

        if (!TryParseStorageMode(request.StorageMode, out var storageMode))
        {
            throw new LayerPublishingException(
                LayerPublishingErrorKind.Validation,
                "Validation failed: storageMode must be 'source' or 'managed'.");
        }

        if (!LayerPublishSourceMetadataBounds.TryValidate(
                request.FieldDomains,
                request.Subtypes,
                request.AttributeRules,
                out var sourceMetadataError))
        {
            throw new LayerPublishingException(
                LayerPublishingErrorKind.Validation,
                $"Validation failed: {sourceMetadataError}");
        }

        return new LayerPublishRequest
        {
            CreateEditableCopy = request.CreateEditableCopy,
            Schema = request.Schema,
            Table = request.Table,
            LayerName = request.LayerName,
            Description = request.Description,
            GeometryColumn = request.GeometryColumn,
            GeometryType = request.GeometryType,
            HasZ = request.HasZ,
            HasM = request.HasM,
            Srid = request.Srid,
            PrimaryKey = request.PrimaryKey,
            GlobalIdField = request.GlobalIdField,
            SupportsAttachments = request.SupportsAttachments,
            Fields = request.Fields ?? Array.Empty<string>(),
            FieldDomains = request.FieldDomains is { } fieldDomains
                ? new Dictionary<string, MetadataV2FieldDomain>(fieldDomains, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, MetadataV2FieldDomain>(StringComparer.OrdinalIgnoreCase),
            Subtypes = request.Subtypes,
            AttributeRules = request.AttributeRules,
            ServiceName = request.ServiceName,
            Namespace = request.Namespace,
            ConnectionId = connectionId == Guid.Empty ? null : connectionId,
            Enabled = request.Enabled,
            SourceGovernance = sourceGovernance,
            StorageMode = storageMode,
            Capabilities = request.Capabilities
        };
    }

    internal static PublishLayerRequest ToHttp(LayerPublishRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new PublishLayerRequest
        {
            CreateEditableCopy = request.CreateEditableCopy,
            Schema = request.Schema,
            Table = request.Table,
            LayerName = request.LayerName,
            Description = request.Description,
            License = request.SourceGovernance?.License,
            Attribution = request.SourceGovernance?.Attribution,
            Publisher = request.SourceGovernance?.Publisher,
            LicenseUrl = request.SourceGovernance?.LicenseUrl,
            SourceUrl = request.SourceGovernance?.SourceUrl,
            GeometryColumn = request.GeometryColumn,
            GeometryType = request.GeometryType,
            HasZ = request.HasZ,
            HasM = request.HasM,
            Srid = request.Srid,
            PrimaryKey = request.PrimaryKey,
            GlobalIdField = request.GlobalIdField,
            SupportsAttachments = request.SupportsAttachments,
            Fields = request.Fields ?? Array.Empty<string>(),
            FieldDomains = request.FieldDomains,
            Subtypes = request.Subtypes,
            AttributeRules = request.AttributeRules,
            ServiceName = request.ServiceName,
            Namespace = request.Namespace,
            Enabled = request.Enabled,
            StorageMode = request.StorageMode == LayerStorageMode.Managed ? "managed" : "source",
            Capabilities = request.Capabilities
        };
    }
}
