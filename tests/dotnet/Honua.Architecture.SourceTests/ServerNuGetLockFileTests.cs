// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace Honua.Architecture.Tests;

// Shipped server images restore from eng/nuget-locks in NuGet locked mode (eng/Honua.ServerLockFiles.props).
// A version bump in Directory.Packages.props without regenerating the locks fails here, before an image build.
[Trait("Category", "Architecture")]
public sealed class ServerNuGetLockFileTests
{
    private const string Regenerate = "regenerate with scripts/ci/regenerate-server-lock-files.sh on the build host";

    private static readonly string[] ShippedVariants = ["serving-postgis", "serving-full", "aot-postgis", "lambda", "lambda-aot"];

    private static readonly string[] ContainerRuntimes = ["linux-x64", "linux-arm64"];

    private static readonly (string Path, string Kind)[] ServerDockerfiles =
    [
        ("Dockerfile", "serving"),
        ("docker/Dockerfile.aot", "aot"),
        ("docker/Dockerfile.lambda", "lambda"),
        ("docker/Dockerfile.lambda.aot", "lambda-aot"),
    ];

    [ArchitectureTest]
    public void EveryShippedVariant_HasALockForEachContainerArchitecture()
    {
        foreach (var (variant, runtime) in Locks())
        {
            LockTargets(variant, runtime).Select(target => target.Name)
                .Should().Contain(name => name.EndsWith($"/{runtime}", StringComparison.Ordinal), $"{variant} {runtime}");
        }
    }

    [ArchitectureTest]
    public void LockedVersions_MatchDirectoryPackagesProps()
    {
        var central = CentralVersions();
        var stale = Locks()
            .SelectMany(lockFile => RequestedRanges(lockFile.Variant, lockFile.Runtime)
                .Select(range => (variant: $"{lockFile.Variant} {lockFile.Runtime}", range.Package, range.Requested)))
            .Where(entry => central.TryGetValue(entry.Package, out var version) && entry.Requested != $"[{version}, )")
            .Select(entry => $"{entry.variant}: {entry.Package} {entry.Requested} != {central[entry.Package]}")
            .ToList();

        stale.Should().BeEmpty(Regenerate);
    }

    [ArchitectureTest]
    public void ServerDockerfiles_PassTheLockVariantToEveryServerRestore()
    {
        var root = ArchitectureTestHelpers.ResolveRepositoryRoot();
        foreach (var (path, kind) in ServerDockerfiles)
        {
            var dockerfile = File.ReadAllText(Path.Join(root, path));
            var restores = Count(dockerfile, "restore-dotnet-with-github-packages.sh src/Honua.Server/Honua.Server.csproj");

            restores.Should().BePositive(path);
            Count(dockerfile, $"server-lock-args.sh {kind})").Should().Be(restores, path);
        }
    }

    // The locks pin SDK-implied packages, so every locked image must build with one SDK.
    [ArchitectureTest]
    public void ServerDockerfiles_ShareOneSdkImage()
    {
        var root = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var sdkImages = ServerDockerfiles
            .Select(dockerfile => File.ReadLines(Path.Join(root, dockerfile.Path))
                .Single(line => line.StartsWith("ARG DOTNET_SDK_IMAGE=", StringComparison.Ordinal)))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        sdkImages.Should().ContainSingle("a different SDK in one server Dockerfile needs its own locks");
    }

    [ArchitectureTest]
    public void Regeneration_CoversEveryShippedVariant()
    {
        var root = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var script = File.ReadAllText(Path.Join(root, "scripts", "ci", "regenerate-server-lock-files.sh"));
        var helper = File.ReadAllText(Path.Join(root, "scripts", "docker", "server-lock-args.sh"));

        script.Should().Contain($"variants=({string.Join(' ', ShippedVariants)})");
        helper.Should().Contain($"  {string.Join('|', ShippedVariants)}) ;;");
    }

    private static int Count(string text, string value)
        => text.Split(value).Length - 1;

    private static IEnumerable<(string Variant, string Runtime)> Locks()
        => ShippedVariants.SelectMany(variant => ContainerRuntimes.Select(runtime => (variant, runtime)));

    private static List<JsonProperty> LockTargets(string variant, string runtime)
    {
        var root = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var path = Path.Join(root, "eng", "nuget-locks", $"honua-server.{variant}.{runtime}.lock.json");
        File.Exists(path).Should().BeTrue($"{path} is missing; {Regenerate}");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("dependencies").Clone().EnumerateObject().ToList();
    }

    // Direct and central-transitive entries carry the requested range; project entries list their packages' ranges.
    private static List<(string Package, string Requested)> RequestedRanges(string variant, string runtime)
    {
        var framework = LockTargets(variant, runtime).Single(target => !target.Name.Contains('/', StringComparison.Ordinal));
        return framework.Value.EnumerateObject().SelectMany(PackageRanges).ToList();
    }

    private static IEnumerable<(string Package, string Requested)> PackageRanges(JsonProperty package)
    {
        if (package.Value.TryGetProperty("requested", out var requested))
        {
            return [(package.Name, requested.GetString()!)];
        }

        var isProject = package.Value.GetProperty("type").GetString() == "Project";
        return isProject && package.Value.TryGetProperty("dependencies", out var dependencies)
            ? dependencies.EnumerateObject().Select(dependency => (dependency.Name, dependency.Value.GetString()!)).ToList()
            : [];
    }

    private static Dictionary<string, string> CentralVersions()
    {
        var root = ArchitectureTestHelpers.ResolveRepositoryRoot();
        return XDocument.Load(Path.Join(root, "Directory.Packages.props"))
            .Descendants()
            .Where(element => element.Name.LocalName == "PackageVersion")
            .ToDictionary(
                element => (string)element.Attribute("Include")!,
                element => (string)element.Attribute("Version")!,
                StringComparer.OrdinalIgnoreCase);
    }
}
