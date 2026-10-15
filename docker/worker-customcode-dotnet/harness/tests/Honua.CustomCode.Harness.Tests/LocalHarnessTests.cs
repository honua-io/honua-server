using System.Text.Json;
using FluentAssertions;
using Honua.CustomCode.Sdk;
using Xunit;

namespace Honua.CustomCode.Harness.Tests;

public sealed class LocalHarnessTests
{
    [Theory]
    [InlineData("success", 0, "succeeded")]
    [InlineData("case", 0, "succeeded")]
    [InlineData("failure", 1, "failed")]
    [InlineData("cancel", 3, "cancelled")]
    [InlineData("grow", 1, "failed")]
    public async Task RunAsync_RealAssembly_RecordsOutcomeAndArtifacts(string mode, int expectedExit, string status)
    {
        var directory = Directory.CreateTempSubdirectory("gp-local-test-");
        try
        {
            var input = Path.Join(directory.FullName, "input.txt");
            await File.WriteAllTextAsync(input, "hello");
            using var parameters = JsonDocument.Parse(JsonSerializer.Serialize(new { mode }));
            var assembly = typeof(LocalFixtureTool).Assembly;
            var output = Path.Join(directory.FullName, "runs");
            var exit = await LocalHarness.RunAsync(Path.GetDirectoryName(assembly.Location)!,
                $"{assembly.GetName().Name}::{typeof(LocalFixtureTool).FullName}", parameters.RootElement,
                new Dictionary<string, string> { ["data"] = input }, output, maxOutputBytes: 10);

            exit.Should().Be(expectedExit);
            var run = Directory.GetDirectories(output).Single();
            using var receipt = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Join(run, "receipt.json")));
            receipt.RootElement.GetProperty("status").GetString().Should().Be(status);
            receipt.RootElement.GetProperty("eventsSchema").GetString().Should().Be(EventJournal.Schema);
            var events = EventJournal.Read(Path.Join(run, "events.jsonl")).ToList();
            events[^1]["kind"]!.GetValue<string>().Should().Be("terminal");
            events[^1]["status"]!.GetValue<string>().Should().Be(status);
            events[^1]["exitCode"]!.GetValue<int>().Should().Be(expectedExit);
            events.Should().OnlyContain(e => e["attempt"]!.GetValue<string>() == "local" && e["v"]!.GetValue<int>() == 1);
            if (expectedExit == 0)
            {
                events.Should().Contain(e => e["kind"]!.GetValue<string>() == "artifact" && e["artifact"]!["name"]!.GetValue<string>() == "answer.txt");
                (await File.ReadAllTextAsync(Path.Join(run, "artifacts", "answer.txt"))).Should().Be("HELLO");
                receipt.RootElement.GetProperty("artifacts")[0].GetProperty("sha256").GetString().Should().HaveLength(64);
            }
            else
            {
                receipt.RootElement.GetProperty("artifacts").GetArrayLength().Should().Be(0);
                if (status == "failed")
                {
                    events[^2]["kind"]!.GetValue<string>().Should().Be("failure");
                }
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Load_RealAssemblyWithSiblingSdk_SharesContractIdentity()
    {
        var assembly = typeof(LocalFixtureTool).Assembly;
        var directory = Path.GetDirectoryName(assembly.Location)!;
        File.Exists(Path.Join(directory, "Honua.CustomCode.Sdk.dll")).Should().BeTrue();
        var tool = new EntrypointLoader().Load(directory,
            $"{assembly.GetName().Name}::{typeof(LocalFixtureTool).FullName}");
        tool.Should().BeAssignableTo<IGeoprocessingTool>();
    }
}

public sealed class LocalFixtureTool : IGeoprocessingTool
{
    public async Task<GpResult> ExecuteAsync(GpContext context, CancellationToken cancellationToken)
    {
        if (context.Client is not null)
        {
            throw new InvalidOperationException("Local tools must not receive a cloud client.");
        }

        var mode = context.Params.GetProperty("mode").GetString();
        if (mode == "failure")
        {
            return GpResult.Failed("bad data");
        }

        if (mode == "cancel")
        {
            throw new OperationCanceledException();
        }

        context.Progress.Report(50, "transform");
        var path = Path.Join(context.WorkDirectory, "result.txt");
        var text = await File.ReadAllTextAsync(context.Inputs["data"], cancellationToken);
        await File.WriteAllTextAsync(path, text.ToUpperInvariant(), cancellationToken);
        context.Output.AddArtifact("answer.txt", mode == "case" && OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path);
        if (mode == "grow")
        {
            await File.WriteAllTextAsync(path, new string('x', 100), cancellationToken);
        }

        return GpResult.Succeeded();
    }
}
