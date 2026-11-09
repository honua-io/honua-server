// Summarize one numeric property of a GeoJSON FeatureCollection.
//
// This is the production custom-code contract: IGeoprocessingTool.ExecuteAsync
// receives a GpContext (params, named input paths, a work directory, an output
// sink, progress) and a cancellation token, and returns a GpResult. The same
// assembly runs unchanged under the local filesystem harness and inside the
// custom-code worker.
using System.Text.Json;
using Honua.CustomCode.Sdk;

namespace Honua.Templates;

public sealed class SummaryTool : IGeoprocessingTool
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public async Task<GpResult> ExecuteAsync(GpContext context, CancellationToken cancellationToken)
    {
        using var data = JsonDocument.Parse(await File.ReadAllTextAsync(context.Inputs["features"], cancellationToken));
        if (!data.RootElement.TryGetProperty("type", out var type) || type.GetString() != "FeatureCollection")
        {
            return GpResult.Failed("Expected a GeoJSON FeatureCollection.");
        }

        var field = context.Params.TryGetProperty("field", out var property) ? property.GetString() ?? "value" : "value";
        var values = new List<double>();
        foreach (var feature in data.RootElement.GetProperty("features").EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!feature.TryGetProperty("properties", out var properties) || !properties.TryGetProperty(field, out var raw))
            {
                return GpResult.Failed($"Feature is missing the '{field}' property.");
            }

            var value = raw.GetDouble(); // Set a breakpoint here.
            if (!double.IsFinite(value))
            {
                return GpResult.Failed("Values must be finite.");
            }

            values.Add(value);
        }

        context.Progress.Report(75, "summarize");
        var summary = new SummaryArtifact(values.Count, field, values.Count == 0 ? null : values.Average(), values.Sum());
        var path = Path.Join(context.WorkDirectory, "summary.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(summary, Json) + "\n", cancellationToken);
        context.Output.AddArtifact("summary.json", path);
        return GpResult.Succeeded();
    }

    /// <summary>Keys are emitted in this declaration order; keep it stable because the golden test compares bytes.</summary>
    private sealed record SummaryArtifact(int count, string field, double? mean, double sum);
}
