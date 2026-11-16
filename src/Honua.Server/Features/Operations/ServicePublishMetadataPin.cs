// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Operations.Domain;

namespace Honua.Server.Features.Operations;

// Server-set publication pin sealed into the approval payload with the other parameters.
internal static class ServicePublishMetadataPin
{
    public const string RevisionKey = "pinnedMetadataRevision";

    public const string EtagKey = "pinnedMetadataEtag";

    public static bool HasAny(OperationRequest request)
        => request.Parameters.ContainsKey(RevisionKey) || request.Parameters.ContainsKey(EtagKey);

    public static IReadOnlyDictionary<string, string?> With(
        IReadOnlyDictionary<string, string?> parameters, LayerPublicationBase publicationBase)
        => new Dictionary<string, string?>(parameters, StringComparer.Ordinal)
        {
            [RevisionKey] = publicationBase.Revision.ToString(CultureInfo.InvariantCulture),
            [EtagKey] = publicationBase.Etag,
        };

    public static LayerPublicationBase? TryRead(OperationRequest request)
        => request.Parameters.TryGetValue(RevisionKey, out var revision)
            && long.TryParse(revision, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            && request.Parameters.TryGetValue(EtagKey, out var etag) && !string.IsNullOrEmpty(etag)
                ? new LayerPublicationBase(parsed, etag)
                : null;

    public static LayerPublicationBase Require(OperationRequest request)
        => TryRead(request)
            ?? throw new InvalidOperationException("The approved publication is missing its approved metadata base.");
}
