// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Operations.Domain;

namespace Honua.Geoprocessing;

// Server-set binding sealed into the approval payload; callers can never supply it.
internal sealed record StagedImportPin(ServerJobArtifactStagePreview Preview, DateTimeOffset ExpiresAt)
{
    private const string CommandKey = "pinnedCommandFingerprint";
    private const string ContentKey = "pinnedContentSha256";
    private const string CountKey = "pinnedFeatureCount";
    private const string SridKey = "pinnedSourceSrid";
    private const string CrsKey = "pinnedCrsState";
    private const string ExpiresKey = "pinnedExpiresAt";

    private static readonly string[] Keys = [CommandKey, ContentKey, CountKey, SridKey, CrsKey, ExpiresKey];

    public Dictionary<string, string?> Parameters() => new(StringComparer.Ordinal)
    {
        [CommandKey] = Preview.CommandFingerprint,
        [ContentKey] = Preview.ContentSha256,
        [CountKey] = Preview.FeatureCount.ToString(CultureInfo.InvariantCulture),
        [SridKey] = Preview.SourceSrid.ToString(CultureInfo.InvariantCulture),
        [CrsKey] = Preview.CrsState,
        [ExpiresKey] = ExpiresAt.ToString("O", CultureInfo.InvariantCulture),
    };

    public static bool HasAny(OperationRequest request) => Keys.Any(request.Parameters.ContainsKey);

    public static StagedImportPin? TryRead(OperationRequest request)
    {
        try
        {
            return Read(request);
        }
        catch (GeoprocessingPreconditionFailedException)
        {
            // A missing or malformed binding is reported as invalid by the caller.
            return null;
        }
    }

    public static StagedImportPin Read(OperationRequest request)
    {
        if (!Keys.All(key => request.Parameters.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)))
        {
            throw new GeoprocessingPreconditionFailedException("The staged import is missing its approved preview binding.");
        }

        var p = request.Parameters;
        if (!long.TryParse(p[CountKey], NumberStyles.Integer, CultureInfo.InvariantCulture, out var count)
            || !int.TryParse(p[SridKey], NumberStyles.Integer, CultureInfo.InvariantCulture, out var srid)
            || !DateTimeOffset.TryParseExact(p[ExpiresKey], "O", CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var expires))
        {
            throw new GeoprocessingPreconditionFailedException("The staged import preview binding is malformed.");
        }

        return new StagedImportPin(new ServerJobArtifactStagePreview(p[CommandKey]!, p[ContentKey]!)
        {
            FeatureCount = count,
            SourceSrid = srid,
            CrsState = p[CrsKey]!,
        }, expires);
    }

    public bool IsExpired(TimeProvider time) => time.GetUtcNow() >= ExpiresAt;
}
