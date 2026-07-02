// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Capabilities;

/// <summary>
/// The broad kind of a <see cref="CapabilityDescriptor"/>. This is the top-level
/// axis every downstream surface (SDK projections, Console binding) groups
/// capabilities by, so it is part of the frozen contract (ADR-0058).
/// </summary>
public enum CapabilityKind
{
    /// <summary>
    /// A platform feature capability — an edition-gated or manifest-advertised
    /// behaviour (for example <c>temporal.filtering</c> or <c>edit.features</c>).
    /// </summary>
    Feature = 0,

    /// <summary>
    /// A protocol operation — an operation or resource exposed on a protocol
    /// surface (for example an <c>/mcp</c> tool/resource or
    /// <c>protocol.odata.batch</c>).
    /// </summary>
    ProtocolOperation = 1,

    /// <summary>
    /// A data format the platform can read and/or write (for example
    /// <c>format.geoparquet</c>).
    /// </summary>
    DataFormat = 2,
}
