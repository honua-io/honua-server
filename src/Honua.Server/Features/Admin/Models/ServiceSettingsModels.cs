// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Server.Features.Admin.Models;

internal sealed class ServiceSettingsResponse
{
    public required string ServiceName { get; init; }

    public required string[] EnabledProtocols { get; init; }

    public required string[] AvailableProtocols { get; init; }

    public AccessPolicyResponse? AccessPolicy { get; init; }

    public TimeInfoResponse? TimeInfo { get; init; }

    public required MapServerSettingsResponse MapServer { get; init; }
}

internal sealed class AccessPolicyResponse
{
    public bool AllowAnonymous { get; init; }

    public bool AllowAnonymousWrite { get; init; }

    public string[]? AllowedRoles { get; init; }

    public string[]? AllowedWriteRoles { get; init; }
}

internal sealed class TimeInfoResponse
{
    public string? StartTimeField { get; init; }

    public string? EndTimeField { get; init; }

    public string? TrackIdField { get; init; }
}

internal sealed class UpdateAccessPolicyRequest
{
    // Optional exact metadata ETag returned by settings inspection. A stale value rejects the update.
    public string? ExpectedMetadataEtag { get; init; }

    public bool? AllowAnonymous { get; init; }

    public bool? AllowAnonymousWrite { get; init; }

    public string[]? AllowedRoles { get; init; }

    public string[]? AllowedWriteRoles { get; init; }
}

internal sealed class UpdateTimeInfoRequest
{
    public string? StartTimeField { get; init; }

    public string? EndTimeField { get; init; }

    public string? TrackIdField { get; init; }
}

internal sealed class LayerMetadataResponse
{
    public MetadataV2ObjectEditing? Editing { get; init; }

    public required int ServiceLayerIndex { get; init; }

    public required string LayerName { get; init; }

    public string? License { get; init; }

    public string? Attribution { get; init; }

    public string? Publisher { get; init; }

    public string? LicenseUrl { get; init; }

    public string? SourceUrl { get; init; }

    public AccessPolicyResponse? AccessPolicy { get; init; }

    public TimeInfoResponse? TimeInfo { get; init; }

    public RasterMosaicResponse? RasterMosaic { get; init; }
}

internal sealed class UpdateLayerMetadataRequest
{
    public MetadataV2EditingPatch? Editing { get; init; }

    public string? License { get; init; }

    public string? Attribution { get; init; }

    public string? Publisher { get; init; }

    public string? LicenseUrl { get; init; }

    public string? SourceUrl { get; init; }

    public UpdateAccessPolicyRequest? AccessPolicy { get; init; }

    public UpdateTimeInfoRequest? TimeInfo { get; init; }

    public UpdateRasterMosaicRequest? RasterMosaic { get; init; }
}

internal sealed class RasterMosaicResponse
{
    public string? MergeStrategy { get; init; }
}

internal sealed class UpdateRasterMosaicRequest
{
    public string? MergeStrategy { get; init; }
}

internal sealed class MapServerSettingsResponse
{
    public int MaxImageWidth { get; init; }

    public int MaxImageHeight { get; init; }

    public int DefaultImageWidth { get; init; }

    public int DefaultImageHeight { get; init; }

    public int DefaultDpi { get; init; }

    public required string DefaultFormat { get; init; }

    public bool DefaultTransparent { get; init; }

    public int MaxFeaturesPerLayer { get; init; }
}

internal sealed class UpdateProtocolsRequest
{
    public required string[] EnabledProtocols { get; init; }
}

internal sealed class UpdateMapServerSettingsRequest
{
    public int? MaxImageWidth { get; init; }

    public int? MaxImageHeight { get; init; }

    public int? DefaultImageWidth { get; init; }

    public int? DefaultImageHeight { get; init; }

    public int? DefaultDpi { get; init; }

    public string? DefaultFormat { get; init; }

    public bool? DefaultTransparent { get; init; }

    public int? MaxFeaturesPerLayer { get; init; }
}

internal sealed class ServiceSummary
{
    public IReadOnlyList<string> FolderMutationIssues { get; init; } = [];
    public string? FolderId { get; init; }
    public string? FolderName { get; init; }
    public string? PublicName { get; init; }
    public string? Title { get; init; }

    public required string ServiceName { get; init; }

    public required string Description { get; init; }

    public int LayerCount { get; init; }

    public string[]? EnabledProtocols { get; init; }
}
