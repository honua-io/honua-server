// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Spec.Domain;

namespace Honua.Core.Features.Spec.Abstractions;

/// <summary>
/// Serializes a <see cref="SpecDocument"/> to its canonical JSON form. The
/// canonical form is the authoritative wire format — text is a projection.
/// </summary>
public interface ISpecCanonicalizer
{
    /// <summary>
    /// Serializes to UTF-8 bytes (canonical hashing input).
    /// </summary>
    /// <param name="document">AST.</param>
    /// <param name="indent"><c>true</c> for pretty-printed output (tools only).</param>
    /// <returns>Canonical JSON bytes.</returns>
    byte[] ToUtf8(SpecDocument document, bool indent = false);

    /// <summary>
    /// Serializes to a UTF-16 string.
    /// </summary>
    /// <param name="document">AST.</param>
    /// <param name="indent"><c>true</c> for pretty-printed output.</param>
    /// <returns>Canonical JSON string.</returns>
    string ToJson(SpecDocument document, bool indent = false);
}
