// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Plugins.Abstractions;

/// <summary>
/// A protocol-neutral description of one attribute column an <see cref="IFeatureOutputFormat"/> is
/// asked to project (issue #2856). Carries just enough schema for a writer to emit a header and
/// look attribute values up by name from a feature's attribute bag, without coupling the public
/// plugin SDK to the host's metadata model. The <see cref="TypeName"/> is an optional, advisory
/// canonical type token (for example <c>"string"</c>, <c>"double"</c>, <c>"datetime"</c>); writers
/// that only need the name can ignore it.
/// </summary>
/// <param name="Name">The attribute name, matching a key in <c>Feature.Attributes</c>.</param>
/// <param name="TypeName">Optional advisory canonical type token; <see langword="null"/> when unknown.</param>
/// <param name="Nullable">Whether the source field is nullable.</param>
public sealed record FeatureOutputField(string Name, string? TypeName, bool Nullable);
