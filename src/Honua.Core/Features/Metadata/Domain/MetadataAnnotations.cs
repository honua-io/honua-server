// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Metadata.Domain;

/// <summary>
/// Well-known annotation keys for metadata resources.
/// </summary>
public static class MetadataAnnotations
{
    /// <summary>
    /// Annotation key for tracking the last applied manifest hash.
    /// </summary>
    public const string LastAppliedManifestHash = "honua.io/last-applied-manifest-hash";

    /// <summary>
    /// Annotation key for tracking up-conversion origin.
    /// </summary>
    public const string UpConvertedFrom = "honua.io/up-converted-from";
}
