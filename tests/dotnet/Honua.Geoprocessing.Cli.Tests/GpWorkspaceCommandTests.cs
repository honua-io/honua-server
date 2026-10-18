// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Honua.Geoprocessing.Cli;
using Xunit;

namespace Honua.Geoprocessing.Cli.Tests;

/// <summary>Installed workspace payload integrity and shell-free transport launch.</summary>
[Trait("Tier", "Fast")]
public sealed class GpWorkspaceCommandTests
{
    [Fact]
    public void Bootstrap_ExtractsExactBundledAdapterWithPerFileProvenance()
    {
        var directory = Directory.CreateTempSubdirectory("gp-workspace-");
        try
        {
            GpWorkspaceCommand.Extract(directory.FullName);
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Join(directory.FullName, "workspace-provenance.json")));
            foreach (var (name, expected) in GpWorkspaceCommand.Resources())
            {
                var bytes = File.ReadAllBytes(Path.Join(directory.FullName, name));
                bytes.Should().Equal(expected);
                manifest.RootElement.GetProperty("files").GetProperty(name).GetString()
                    .Should().Be(Convert.ToHexStringLower(SHA256.HashData(bytes)));
            }
            manifest.RootElement.GetProperty("devkitVersion").GetString().Should().NotBeNullOrWhiteSpace();
            Action overwrite = () => GpWorkspaceCommand.Extract(directory.FullName);
            overwrite.Should().Throw<GpCliUsageException>();
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Bootstrap_CleansPartialPayloadAfterExtractionFailure()
    {
        var parent = Directory.CreateTempSubdirectory("gp-workspace-failure-");
        var output = Path.Join(parent.FullName, "payload");
        var writes = 0;
        try
        {
            Action extract = () => GpWorkspaceCommand.Extract(output, (path, bytes) =>
            {
                File.WriteAllBytes(path, bytes);
                if (++writes == 2) throw new IOException("Simulated full volume.");
            });
            extract.Should().Throw<IOException>();
            Directory.Exists(output).Should().BeFalse();
        }
        finally { parent.Delete(recursive: true); }
    }

    [Fact]
    public void Serve_ResolvesSymlinkAncestorsBeforeComparingProjectAndPayload()
    {
        if (OperatingSystem.IsWindows()) return;
        var parent = Directory.CreateTempSubdirectory("gp-workspace-link-");
        try
        {
            var actual = Directory.CreateDirectory(Path.Join(parent.FullName, "actual", "project"));
            Directory.CreateSymbolicLink(Path.Join(parent.FullName, "alias"), Path.Join(parent.FullName, "actual"));
            GpWorkspaceCommand.ResolvedDirectory(Path.Join(parent.FullName, "alias", "project"))
                .Should().Be(actual.FullName);
        }
        finally { parent.Delete(recursive: true); }
    }

    [Fact]
    public void Serve_KeepsPathsAsArgumentsAndNeverPlacesCredentialInCommandLine()
    {
        var arguments = new[] { "--project", "/project with spaces/$(touch ignored)", "--image", "sha256:" + new string('a', 64) };
        var start = GpWorkspaceCommand.StartInfo("/installed payload", arguments);
        start.UseShellExecute.Should().BeFalse();
        start.FileName.Should().Be("python3");
        start.ArgumentList.Should().Equal(new[] { Path.Join("/installed payload", "workspace_service.py") }.Concat(arguments));
        start.ArgumentList.Should().NotContain(argument => argument.Contains("WORKSPACE_KEY", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Bootstrap_RejectsUnexpectedOptionsBeforeWriting()
    {
        Func<Task> invalid = () => GpWorkspaceCommand.RunAsync(["bootstrap", "--url", "https://untrusted.invalid"]);
        await invalid.Should().ThrowAsync<GpCliUsageException>();
    }
}
