// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.IO.Pipelines;
using System.Text;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Protocols.Ogc.Api.Features.Services;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Features.Services;

/// <summary>
/// Verifies publisher-hidden fields are omitted while supported protocol behavior is preserved.
/// </summary>
public sealed class OgcResponseFormatterHiddenFieldTests
{
    [UnitTheory]
    [InlineData("single")]
    [InlineData("collection")]
    [InlineData("stream")]
    [InlineData("buffered-stream")]
    public async Task Gml_ProviderReturnsHiddenAttributes_OmitsTheirValues(string format)
    {
        var feature = GmlFeature.Create(1, null, ImmutableDictionary<string, object?>.Empty
            .Add("name", "visible-5614")
            .Add("CATEGORY", "hidden-5614"));
        var hiddenFields = ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "category");
        string payload;
        if (format == "single")
        {
            payload = OgcResponseFormatter.BuildGmlSingleFeature(feature, hiddenFields: hiddenFields);
        }
        else if (format == "collection")
        {
            payload = OgcResponseFormatter.BuildGmlFeatureCollection([feature], hiddenFields: hiddenFields);
        }
        else
        {
            await using var stream = new MemoryStream();
            var writer = PipeWriter.Create(stream, new StreamPipeWriterOptions(leaveOpen: true));
            if (format == "stream")
            {
                await OgcResponseFormatter.StreamGmlFeatureCollectionAsync(
                    OneFeature(feature), writer, 1, 1, null, null, null,
                    AxisOrder.EastNorth, CancellationToken.None, hiddenFields);
            }
            else
            {
                await OgcResponseFormatter.StreamGmlFeatureCollectionAsync(
                    new List<GmlFeature> { feature }, writer, 1, null, null, null,
                    AxisOrder.EastNorth, CancellationToken.None, hiddenFields);
            }

            await writer.CompleteAsync();
            payload = Encoding.UTF8.GetString(stream.ToArray());
        }

        Assert.Contains("visible-5614", payload);
        Assert.DoesNotContain("hidden-5614", payload);
        Assert.DoesNotContain("CATEGORY", payload);
    }

    private static async IAsyncEnumerable<GmlFeature> OneFeature(GmlFeature feature)
    {
        await Task.Yield();
        yield return feature;
    }
}
