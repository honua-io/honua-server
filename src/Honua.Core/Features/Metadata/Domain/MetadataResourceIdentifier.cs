// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Metadata.Domain;

/// <summary>
/// Identifies a metadata resource by kind, namespace, and name.
/// </summary>
/// <param name="Kind">Resource kind.</param>
/// <param name="Namespace">Resource namespace.</param>
/// <param name="Name">Resource name.</param>
public sealed record MetadataResourceIdentifier(string Kind, string Namespace, string Name);
