// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Import.Domain;

/// <summary>Dataset-import source kinds. Each kind carries its own reference and cannot borrow another kind's provenance.</summary>
public static class DatasetImportSourceKinds
{
    /// <summary>A file selected in the human browser. Bytes are not a job artifact.</summary>
    public const string BrowserFile = "browser-file";

    /// <summary>An output of an authorized local process run. Not a server filesystem path.</summary>
    public const string LocalProcessArtifact = "local-process-artifact";

    /// <summary>An already authorized completed Server job artifact, identified by immutable job identities.</summary>
    public const string ServerJobArtifact = "server-job-artifact";

    /// <summary>The three canonical source kinds. No other kind is a dataset-import source.</summary>
    public static readonly IReadOnlyList<string> All = [BrowserFile, LocalProcessArtifact, ServerJobArtifact];
}

/// <summary>Exact Server-owned result identity. Callers do not supply a location or bulk content.</summary>
/// <param name="TargetId">Deployment target the selection is bound to.</param>
/// <param name="JobId">Completed built-in job.</param>
/// <param name="ResultPackageId">Immutable result package version for that job.</param>
/// <param name="ArtifactId">Artifact identity within that package.</param>
/// <param name="ReferenceSha256">SHA-256 of the server-stored artifact reference, not of a caller path.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DatasetImportServerJobArtifactReference(
    string TargetId,
    string JobId,
    string ResultPackageId,
    string ArtifactId,
    string ReferenceSha256);

/// <summary>
/// Rejects source declarations that mix kinds or invent a URI, path, or another kind's provenance.
/// Admission of bytes for browser-file and local-process-artifact stays on their own paths.
/// </summary>
public static class DatasetImportSourceRules
{
    /// <summary>
    /// Ensures <paramref name="kind"/> is one of the canonical variants.
    /// Browser-file and local-process-artifact are recognized and refused here:
    /// this handoff only admits an authorized server job artifact.
    /// </summary>
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
