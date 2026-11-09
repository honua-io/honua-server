// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Import.Domain;

// Each source kind carries its own reference and cannot borrow another kind's provenance.
public static class DatasetImportSourceKinds
{
    public const string BrowserFile = "browser-file";

    public const string LocalProcessArtifact = "local-process-artifact";

    public const string ServerJobArtifact = "server-job-artifact";

    public static readonly IReadOnlyList<string> All = [BrowserFile, LocalProcessArtifact, ServerJobArtifact];
}

// The artifact digest is of the server-stored reference, not of a caller path.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DatasetImportServerJobArtifactReference(
    string TargetId,
    string JobId,
    string ResultPackageId,
    string ArtifactId,
    string ReferenceSha256);

public static class DatasetImportSourceRules
{
    public static void RequireServerJobArtifact(string? kind)
    {
        if (kind is not (
            DatasetImportSourceKinds.BrowserFile
            or DatasetImportSourceKinds.LocalProcessArtifact
            or DatasetImportSourceKinds.ServerJobArtifact))
        {
            throw new ArgumentException(
                "source.kind must be browser-file, local-process-artifact, or server-job-artifact.");
        }

        if (!string.Equals(kind, DatasetImportSourceKinds.ServerJobArtifact, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "browser-file and local-process-artifact are not server job artifacts. "
                + "This handoff will not accept a browser selection, local-run receipt, URI, or filesystem path in their place.");
        }
    }
}
