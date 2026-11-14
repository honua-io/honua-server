// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.Import.Abstractions;
using Honua.Geoprocessing.Execution;

namespace Honua.Geoprocessing;

internal sealed record ServerJobArtifactImportContext(
    ClaimsPrincipal Principal, IGeoprocessingJobService Jobs, IFileImportService Imports)
{
    internal required IServiceProvider Services { get; init; }
}

internal sealed record ServerJobArtifactStagePreview(string CommandFingerprint, string ContentSha256)
{
    internal required long FeatureCount { get; init; }
    internal required int SourceSrid { get; init; }
    internal required string CrsState { get; init; }
}

internal static partial class ServerJobArtifactImporter
{
    internal static async Task<ServerJobArtifactStagePreview> PreviewAsync(
        ServerJobArtifactImportCommand command, ServerJobArtifactImportContext context,
        CancellationToken cancellationToken)
    {
        ValidateCommand(command);
        var authorized = await ReadAuthorizedAsync(command, context.Principal, context.Jobs,
            context.Services, context.Imports.Limits.MaxMemoryBytes, cancellationToken).ConfigureAwait(false);
        await using (authorized.Content.ConfigureAwait(false))
        {
            var latest = await ReadJobAsync(command.Reference.JobId, context.Principal, context.Jobs,
                cancellationToken).ConfigureAwait(false);
            if (!SameBinding(latest, authorized.Job, authorized.RawReference))
            {
                throw new GeoprocessingPreconditionFailedException("The job artifact changed during preview.");
            }

            var summary = authorized.Summary;
            var sourceSrid = ResolveSourceSrid(summary.SpatialReference.State,
                summary.SpatialReference.Srid, command.SourceSrid);
            return new ServerJobArtifactStagePreview(PreviewCommandFingerprint(command), summary.Sha256)
            {
                FeatureCount = summary.FeatureCount,
                SourceSrid = sourceSrid,
                CrsState = summary.SpatialReference.State,
            };
        }
    }

    private static void ValidatePreviewBinding(ServerJobArtifactImportCommand command)
    {
        if (command.StagePreview is { } preview
            && !string.Equals(preview.CommandFingerprint, PreviewCommandFingerprint(command), StringComparison.Ordinal))
        {
            throw new GeoprocessingPreconditionFailedException("The import no longer matches its preview.");
        }
    }

    private static void ValidatePreviewContent(
        ServerJobArtifactStagePreview? preview, GeoprocessingImportContentSummary summary, int sourceSrid)
    {
        if (preview is not null
            && (preview.ContentSha256 != summary.Sha256 || preview.FeatureCount != summary.FeatureCount
                || preview.SourceSrid != sourceSrid || preview.CrsState != summary.SpatialReference.State))
        {
            throw new GeoprocessingPreconditionFailedException("The source content no longer matches its preview.");
        }
    }

    private static string PreviewCommandFingerprint(ServerJobArtifactImportCommand command)
        => HashFingerprint(string.Join("\n", command.ActorId, command.IntentId, Fingerprint(command)));

    private static string ImportFingerprint(ServerJobArtifactImportCommand command)
        => command.StagePreview is not { } preview ? Fingerprint(command) : PreviewBindingFingerprint(preview);

    // Binds the command scope and the verified content; an agent presents it when proposing the import.
    internal static string PreviewBindingFingerprint(ServerJobArtifactStagePreview preview)
        => HashFingerprint(string.Join("\n",
            "create-only/v1", preview.CommandFingerprint, preview.ContentSha256,
            preview.FeatureCount.ToString(CultureInfo.InvariantCulture),
            preview.SourceSrid.ToString(CultureInfo.InvariantCulture), preview.CrsState));

    private static string HashFingerprint(string value)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
