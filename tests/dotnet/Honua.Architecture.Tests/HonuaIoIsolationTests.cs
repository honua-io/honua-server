// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace Honua.Architecture.Tests;

/// <summary>
/// Enforces the Honua.Io isolation contract: the carved-out file input/output
/// surface (file storage today; export + upload primitives planned) must never
/// take a ProjectReference on Honua.Server, directly or transitively. The whole
/// reason Honua.Io exists is to flip that edge — Server references Io, never the
/// reverse — so the host's composition root no longer carries the file-storage
/// services, upload-progress stores, and retention cleanup background service.
/// </summary>
/// <remarks>
/// Io is ASP.NET-coupled (it owns HTTP endpoints and a BackgroundService) and
/// depends on Hosting. It deliberately does NOT carry the NTS file-format
/// readers — those stay in the light Honua.Geometry so the storage providers
/// can consume them without pulling this module's ASP.NET surface.
/// </remarks>
[Trait("Category", "Architecture")]
public sealed class HonuaIoIsolationTests
{
    private const string HonuaIoCsprojRelativePath = "src/Honua.Io/Honua.Io.csproj";

    private static readonly HashSet<string> PermittedProviders = new(StringComparer.Ordinal)
    {
        "Honua.Core.Abstractions",
        "Honua.Core",
        "Honua.Hosting",
        "Honua.ServiceDefaults",
    };

    [ArchitectureTest]
    public void HonuaIo_ShouldNotReference_HonuaServer()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var csprojPath = Path.Combine(repositoryRoot, HonuaIoCsprojRelativePath.Replace('/', Path.DirectorySeparatorChar));

        File.Exists(csprojPath).Should().BeTrue(
            "Honua.Io.csproj must exist at the canonical path: {0}", csprojPath);

        var referenced = ReferencedProjectNames(csprojPath);

        referenced.Should().NotContain(
            "Honua.Server",
            "Honua.Io must never reference Honua.Server. The whole point of the io-split is to " +
            "flip that edge so Server depends on Io, not the reverse.");
    }

    [ArchitectureTest]
    public void HonuaIo_MustOnlyReference_PermittedProviders()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var csprojPath = Path.Combine(repositoryRoot, HonuaIoCsprojRelativePath.Replace('/', Path.DirectorySeparatorChar));

        var referenced = ReferencedProjectNames(csprojPath);

        referenced.Should().NotBeEmpty(
            "Honua.Io must declare at least one ProjectReference (Core, Hosting, …) — an empty " +
            "reference set indicates the csproj was rewritten without the expected scaffolding.");

        var disallowed = referenced.Where(name => !PermittedProviders.Contains(name)).ToList();

        disallowed.Should().BeEmpty(
            "Honua.Io may only reference Honua.Core.Abstractions, Honua.Core, Honua.Hosting, and " +
            "Honua.ServiceDefaults. When the export increment lands it may add Honua.Geometry; " +
            "update this allow-list and the ADR-0047 matrix together. Disallowed references: {0}",
            string.Join(", ", disallowed));
    }

    private static List<string> ReferencedProjectNames(string csprojPath)
        => XDocument.Load(csprojPath)
            .Descendants("ProjectReference")
            .Select(element => element.Attribute("Include")?.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => Path.GetFileNameWithoutExtension(value!.Replace('\\', '/'))!)
            .ToList();
}
