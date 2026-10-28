// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Honua.Geoprocessing.Diagnostics;
using Xunit;

namespace Honua.Geoprocessing.Cli.Tests;

/// <summary>
/// <c>honua-gp-devkit reproduce</c> (honua-server#6220): an authorized export runs locally,
/// and a missing input, a hash mismatch, an old bundle, credential material, or a failure
/// that does not match the recorded native outcome is refused. No server is contacted.
/// </summary>
[Collection(GpCliEndToEndTests.CollectionName)]
[global::Xunit.Trait("Tier", "Fast")]
public sealed class GpReproduceCommandTests : IDisposable
{
    // geometry.buffer inputs mirror GpCliEndToEndTests: buffer POINT(0 0) by 10 in EPSG:4326.
    private const string PointWkbBase64 = "AQEAAAAAAAAAAAAAAAAAAAAAAAAA";

    private const string WrongHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private const string Credential = "https://analyst:s3cret-token@db.internal/honua";

    private readonly string _root = Path.Join(Path.GetTempPath(), "honua-gp-reproduce-" + Guid.NewGuid().ToString("N"));

    public GpReproduceCommandTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort; the temp directory is per-test.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort; the temp directory is per-test.
        }
    }

    [Fact]
    public async Task RunAsync_Reproduce_AuthorizedExportedInputs_ReproducesSuccess()
    {
        var bundle = WriteBundle(SuccessInputs(), errorCode: null);

        var (exit, stdout, stderr) = await InvokeAsync("reproduce", bundle);

        exit.Should().Be(0, stdout + stderr);
        stdout.Should().Contain("process : geometry.buffer");
        stdout.Should().Contain("status  : Succeeded");
        stdout.Should().Contain("outcome : reproduced recorded success");
        stderr.Should().NotContain("reproduced recorded failure");
    }

    [Fact]
    public async Task RunAsync_Reproduce_MissingInput_RefusesWithTheMissingName()
    {
        var bundle = WriteBundle(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["parcels"] = "not-exported" },
            errorCode: null,
            omitId: "parcels");

        var (exit, stdout, stderr) = await InvokeAsync("reproduce", bundle);

        exit.Should().NotBe(0);
        stderr.Should().Contain("input 'parcels'").And.Contain("actual missing");
        stdout.Should().NotContain("reproduced");
        stderr.Should().NotContain("reproduced");
    }

    [Fact]
    public async Task RunAsync_Reproduce_HashMismatch_RefusesBeforeRunning()
    {
        var bundle = WriteBundle(SuccessInputs(), errorCode: null, mismatchId: "wkb");
        var actual = Sha(PointWkbBase64);

        var (exit, stdout, stderr) = await InvokeAsync("reproduce", bundle);

        exit.Should().NotBe(0);
        stderr.Should().Contain("input 'wkb'").And.Contain("expected sha256 " + WrongHash).And.Contain("actual " + actual);
        stdout.Should().NotContain("reproduced");
    }

    [Fact]
    public async Task RunAsync_Reproduce_OldBundleWithoutEvidence_RefusesAsIncomplete()
    {
        var json = """
            {
              "schemaVersion": "1.0",
              "contentClassification": "internal",
              "consent": { "redactionAcknowledged": true, "shareWithSupport": false },
              "envelopes": [ { "method": "GET", "normalizedPath": "/api/v1/jobs/{id}" } ]
            }
            """;
        var path = Path.Join(_root, "old-bundle.json");
        await File.WriteAllTextAsync(path, json);

        var document = GpDiagnosticBundleDocument.Parse(json);
        document.SchemaVersion.Should().Be("1.0");
        document.Evidence.Should().BeNull();

        var (exit, _, stderr) = await InvokeAsync("reproduce", path);

        exit.Should().NotBe(0);
        stderr.Should().Contain("bundle loaded").And.Contain("schemaVersion 1.0").And.Contain("incomplete");
        stderr.Should().Contain("gpEvidence");
        stderr.Should().NotContain("not valid JSON");
    }

    [Fact]
    public async Task RunAsync_Reproduce_CredentialLookingInput_RefusesBeforeRunning()
    {
        var inputs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["source"] = Credential,
        };
        var bundle = WriteBundle(inputs, errorCode: null, referenceId: "source", reference: Credential);

        var (exit, stdout, stderr) = await InvokeAsync("reproduce", bundle);

        exit.Should().NotBe(0);
        stderr.Should().Contain("credential");
        stderr.Should().NotContain("s3cret-token");
        stdout.Should().NotContain("s3cret-token").And.NotContain("reproduced");
    }

    [Fact]
    public async Task RunAsync_Reproduce_RecordedNativeFailure_ReportsReproducedOutcome()
    {
        var inputs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["wkb"] = PointWkbBase64,
            ["srid"] = "4326",
            ["distance"] = "0",
        };
        var bundle = WriteBundle(inputs, errorCode: "Invalid buffer inputs");

        var (exit, stdout, stderr) = await InvokeAsync("reproduce", bundle);

        exit.Should().Be(0, stdout + stderr);
        stdout.Should().Contain("status  : Failed");
        stdout.Should().Contain("outcome : reproduced recorded failure");
        stdout.Should().Contain("code    : Invalid buffer inputs");
        stdout.Should().Contain("Invalid buffer inputs");
        stdout.Should().NotContain("status  : Succeeded");
        stdout.Should().NotContain("reproduced recorded success");
        stderr.Should().NotContain("was not reproduced");
    }

    private string WriteBundle(
        IReadOnlyDictionary<string, string> inputs,
        string? errorCode,
        string? omitId = null,
        string? mismatchId = null,
        string? referenceId = null,
        string? reference = null)
    {
        var inputNodes = new JsonArray();
        var included = new JsonArray();
        foreach (var (id, text) in inputs)
        {
            var hash = string.Equals(id, mismatchId, StringComparison.Ordinal) ? WrongHash : Sha(text);
            var input = new JsonObject
            {
                ["id"] = id,
                ["role"] = "input",
                ["mediaType"] = "text/plain",
                ["byteSize"] = Encoding.UTF8.GetByteCount(text),
                ["sha256"] = hash,
            };
            if (string.Equals(id, referenceId, StringComparison.Ordinal) && reference is not null)
            {
                input["reference"] = reference;
            }

            inputNodes.Add(input);
            if (string.Equals(id, omitId, StringComparison.Ordinal))
            {
                continue;
            }

            included.Add(new JsonObject
            {
                ["id"] = id,
                ["contentIncluded"] = true,
                ["byteSize"] = Encoding.UTF8.GetByteCount(text),
                ["sha256"] = hash,
                ["contentSha256"] = Sha(text),
                ["redactionApplied"] = false,
                ["contentBase64"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(text)),
            });
        }

        var evidence = new JsonObject
        {
            ["schema"] = "honua.gp-diagnostic-evidence/v1",
            ["jobId"] = "job-6220",
            ["inputDataIncluded"] = included.Count > 0,
            ["dependencies"] = new JsonArray
            {
                new JsonObject { ["name"] = "geometry.buffer" },
            },
            ["artifactsState"] = "none",
            ["inputs"] = inputNodes,
        };
        if (included.Count > 0)
        {
            evidence["inputExport"] = new JsonObject
            {
                ["authorizedBy"] = "ada@honua.io",
                ["authorizedAt"] = "2026-10-27T18:40:00Z",
                ["included"] = included,
            };
        }

        if (errorCode is not null)
        {
            evidence["error"] = new JsonObject
            {
                ["message"] = "Invalid buffer inputs: invalid input 'distance'; expected a finite number greater than zero",
                ["classification"] = "execution_failed",
                ["errorCode"] = errorCode,
            };
        }

        var document = new JsonObject
        {
            ["schemaVersion"] = "1.0",
            ["contentClassification"] = "internal",
            ["consent"] = new JsonObject
            {
                ["redactionAcknowledged"] = true,
                ["shareWithSupport"] = false,
            },
            ["envelopes"] = new JsonArray
            {
                new JsonObject
                {
                    ["method"] = "GET",
                    ["normalizedPath"] = "/api/v1/jobs/{id}",
                },
            },
            ["gpEvidence"] = evidence,
        };

        var path = Path.Join(_root, "bundle-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, document.ToJsonString());
        return path;
    }

    private static Dictionary<string, string> SuccessInputs()
        => new(StringComparer.Ordinal)
        {
            ["wkb"] = PointWkbBase64,
            ["srid"] = "4326",
            ["distance"] = "10",
        };

    private static string Sha(string text)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static async Task<CliResult> InvokeAsync(params string[] args)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            var exit = await GpCli.RunAsync(args);
            return new CliResult(exit, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private readonly record struct CliResult(int ExitCode, string Stdout, string Stderr);
}
