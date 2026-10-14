using System.Text.Json;
using Honua.CustomCode.Sdk;

namespace Honua.Samples;

public sealed class SummaryTool : IGeoprocessingTool
{
    public async Task<GpResult> ExecuteAsync(GpContext context, CancellationToken cancellationToken)
    {
        using var data = JsonDocument.Parse(await File.ReadAllTextAsync(context.Inputs["features"], cancellationToken));
        if (data.RootElement.GetProperty("type").GetString() != "FeatureCollection")
        {
            return GpResult.Failed("Expected a GeoJSON FeatureCollection.");
        }

        var field = context.Params.TryGetProperty("field", out var property) ? property.GetString()! : "value";
        var values = new List<double>();
        foreach (var feature in data.RootElement.GetProperty("features").EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = feature.GetProperty("properties").GetProperty(field).GetDouble(); // Set a breakpoint here.
            if (!double.IsFinite(value))
            {
                return GpResult.Failed("Values must be finite.");
            }

            values.Add(value);
        }

        context.Progress.Report(75, "summarize");
        var result = new { count = values.Count, sum = values.Sum(), mean = values.Count == 0 ? (double?)null : values.Average() };
        var path = Path.Join(context.WorkDirectory, "summary.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(result), cancellationToken);
        context.Output.AddArtifact("summary.json", path);
        return GpResult.Succeeded();
    }
}
