// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Plugins.Abstractions;

/// <summary>
/// An advertised, license-gated feature output format contributed by a plugin (issue #2856). The
/// host surfaces these so a format-negotiation seam can list the additional formats a caller may
/// request without resolving the writer itself. Carries the same identity a writer declares on
/// <see cref="IFeatureOutputFormat"/>, plus the owning plugin id for diagnostics.
/// </summary>
/// <param name="FormatId">The stable wire token that selects the format.</param>
/// <param name="MediaType">The media type the host sets on the response.</param>
/// <param name="FileExtension">The file extension (without a leading dot) for the download filename.</param>
/// <param name="PluginId">The id of the plugin that contributed the format.</param>
public sealed record PluginOutputFormatDescriptor(
    string FormatId,
    string MediaType,
    string FileExtension,
    string PluginId);
