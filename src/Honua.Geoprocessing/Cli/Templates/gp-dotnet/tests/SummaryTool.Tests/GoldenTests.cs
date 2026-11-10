// Golden-result tests for the summary process, run through the real local harness.
//
// Each test runs the harness local mode against the built process assembly,
// then asserts on the receipt it wrote. Failure messages name the first
// differing value and the receipt path so a regression is actionable from the
// test output alone.
using System.Security.Cryptography;
using System.Text.Json;
using Honua.CustomCode.Harness;
using Xunit;

namespace Honua.Templates.Tests;

public sealed class GoldenTests
{
    private static readonly string Template = FindTemplateRoot();
    private static readonly string Fixtures = Path.Join(Template, "fixtures");
    private static readonly string Expected = Path.Join(Template, "expected");
    private static readonly string AssemblyDirectory = Path.GetDirectoryName(typeof(SummaryTool).Assembly.Location)!;

    [Fact]
    public async Task Summary_matches_golden()
    {
        using var golden = JsonDocument.Parse(File.ReadAllText(Path.Join(Expected, "golden.json")));
        var artifactName = golden.RootElement.GetProperty("artifact").GetString()!;
        var expectedDigest = golden.RootElement.GetProperty("sha256").GetString()!;

        var (receipt, receiptPath) = await RunAsync(Path.Join(Fixtures, "input.geojson"));

        Assert.True(receipt.GetProperty("status").GetString() == "succeeded",
            $"{receipt.GetProperty("status")} in phase {Field(receipt, "failedPhase")}; receipt: {receiptPath}");
        var artifacts = receipt.GetProperty("artifacts").EnumerateArray().ToList();
        Assert.True(artifacts.Count == 1 && artifacts[0].GetProperty("name").GetString() == artifactName,
            $"artifacts {receipt.GetProperty("artifacts")}; receipt: {receiptPath}");

        var artifactPath = Path.Join(Path.GetDirectoryName(receiptPath)!, artifacts[0].GetProperty("path").GetString()!);
        using var produced = JsonDocument.Parse(File.ReadAllText(artifactPath));
        using var expected = JsonDocument.Parse(File.ReadAllText(Path.Join(Expected, artifactName)));
        var difference = FirstDifference(expected.RootElement, produced.RootElement);
        Assert.True(difference is null, $"first difference {difference}; artifact: {artifactPath}; receipt: {receiptPath}");

        var digest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(artifactPath))).ToLowerInvariant();
        Assert.True(digest == expectedDigest, $"artifact bytes differ: sha256 {digest} != {expectedDigest}; artifact: {artifactPath}");
        Assert.Equal(expectedDigest, artifacts[0].GetProperty("sha256").GetString());
        Assert.False(receipt.GetProperty("eventsTruncated").GetBoolean());
    }

    [Fact]
    public async Task Invalid_input_fails_in_execute_with_receipt()
    {
        var (receipt, receiptPath) = await RunAsync(Path.Join(Fixtures, "invalid-input.geojson"));
        Assert.True(receipt.GetProperty("exitCode").GetInt32() == 1, $"exit {receipt.GetProperty("exitCode")}; receipt: {receiptPath}");
        Assert.Equal("failed", receipt.GetProperty("status").GetString());
        Assert.Equal("execute", receipt.GetProperty("failedPhase").GetString());
        Assert.Equal("Expected a GeoJSON FeatureCollection.", receipt.GetProperty("error").GetProperty("message").GetString()!.Split('\n')[0]);
        Assert.Empty(receipt.GetProperty("artifacts").EnumerateArray());
    }

    [Fact]
    public async Task Output_cap_fails_when_the_artifact_is_registered()
    {
        var (receipt, receiptPath) = await RunAsync(Path.Join(Fixtures, "input.geojson"), maxOutputBytes: 1);
        Assert.True(receipt.GetProperty("exitCode").GetInt32() == 1, $"exit {receipt.GetProperty("exitCode")}; receipt: {receiptPath}");
        Assert.Equal("failed", receipt.GetProperty("status").GetString());
        // The output sink refuses the artifact at registration, so the failure is in the execute phase.
        Assert.Equal("execute", receipt.GetProperty("failedPhase").GetString());
        Assert.Equal("OutputSizeExceededException", receipt.GetProperty("error").GetProperty("type").GetString());
        Assert.StartsWith("Adding artifact 'summary.json'", receipt.GetProperty("error").GetProperty("message").GetString());
        Assert.Empty(receipt.GetProperty("artifacts").EnumerateArray());
    }

    [Fact]
    public async Task Missing_input_is_a_setup_failure()
    {
        var (receipt, _) = await RunAsync(Path.Join(Fixtures, "does-not-exist.geojson"));
        Assert.Equal(2, receipt.GetProperty("exitCode").GetInt32());
        Assert.Equal("setupFailed", receipt.GetProperty("status").GetString());
        Assert.Equal("setup", receipt.GetProperty("failedPhase").GetString());
    }

    private static async Task<(JsonElement Receipt, string ReceiptPath)> RunAsync(string features, long? maxOutputBytes = null)
    {
        var output = Path.Join(Path.GetTempPath(), "gp-dotnet-template-tests", Guid.NewGuid().ToString("N"));
        using var parameters = JsonDocument.Parse(File.ReadAllText(Path.Join(Fixtures, "params.json")));
        var inputs = new Dictionary<string, string>(StringComparer.Ordinal) { ["features"] = features };
        var exitCode = await LocalHarness.RunAsync(
            AssemblyDirectory, "SummaryTool::Honua.Templates.SummaryTool", parameters.RootElement, inputs, output,
            maxOutputBytes ?? 100 * 1024 * 1024);
        var receiptPath = Directory.GetFiles(output, "receipt.json", SearchOption.AllDirectories).Single();
        var receipt = JsonDocument.Parse(File.ReadAllText(receiptPath)).RootElement.Clone();
        Assert.True(receipt.GetProperty("exitCode").GetInt32() == exitCode, $"receipt exit code disagrees with the harness: {receiptPath}");
        return (receipt, receiptPath);
    }

    private static string? FirstDifference(JsonElement expected, JsonElement actual)
    {
        var keys = expected.EnumerateObject().Select(p => p.Name).Union(actual.EnumerateObject().Select(p => p.Name)).Order(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            var left = expected.TryGetProperty(key, out var e) ? e.GetRawText() : "<missing>";
            var right = actual.TryGetProperty(key, out var a) ? a.GetRawText() : "<missing>";
            if (left != right)
            {
                return $"{key}: expected {left}, got {right}";
            }
        }

        return null;
    }

    private static string Field(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) ? value.ToString() : "<none>";

    private static string FindTemplateRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Join(directory.FullName, "Directory.Build.props")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Template root (Directory.Build.props) not found above the test assembly.");
    }
}
