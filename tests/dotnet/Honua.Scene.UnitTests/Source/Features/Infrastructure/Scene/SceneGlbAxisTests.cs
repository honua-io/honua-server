// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using FluentAssertions;
using Honua.Scene.Assets;
using Honua.TestKit.Attributes;
using Xunit;

namespace Honua.Server.Tests.Features.Infrastructure.Scene;

public sealed class SceneGlbAxisTests
{
    [UnitTheory]
    [InlineData(false, null, 2, 3)]
    [InlineData(true, null, -3, 2)]
    [InlineData(false, "Y", -3, 2)]
    [InlineData(true, "Z", 2, 3)]
    public void Read_HonuaAssets_RespectsLegacyAndExplicitUpAxes(bool standardYUp, string? upAxis, double expectedY, double expectedZ)
    {
        var glb = standardYUp ? BuildStandardYUpGlb() : BuildLegacyGlb();
        var meshes = SceneGlbAssetReader.Read(glb, SceneAffineTransform.Identity,
            upAxis, _ => throw new InvalidOperationException(), CancellationToken.None);

        var vertex = meshes.Single().Triangles.Single().A.Position;
        vertex.X.Should().Be(1);
        vertex.Y.Should().Be(expectedY);
        vertex.Z.Should().Be(expectedZ);
    }

    [UnitTheory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"metadata\"")]
    [InlineData("{\"honuaUpAxis\":1}")]
    public void Read_NonAxisExtras_UsesExplicitUpAxis(string extrasJson)
    {
        var meshes = SceneGlbAssetReader.Read(BuildLegacyGlb(extrasJson), SceneAffineTransform.Identity,
            "Y", _ => throw new InvalidOperationException(), CancellationToken.None);

        var vertex = meshes.Single().Triangles.Single().A.Position;
        vertex.X.Should().Be(1);
        vertex.Y.Should().Be(-3);
        vertex.Z.Should().Be(2);
    }

    [UnitTest]
    public void Read_BinaryDagNodeGraph_RejectsWithinNodeVisitBudget()
    {
        var nodes = new List<string>();
        for (var i = 0; i < 31; i++)
        {
            nodes.Add($$"""{"children":[{{i + 1}},{{i + 1}}]}""");
        }

        nodes.Add("""{"mesh":0}""");
        var json = $$$"""
            {"asset":{"version":"2.0"},
             "scenes":[{"nodes":[0]}],"nodes":[{{{string.Join(",", nodes)}}}],
             "meshes":[{"primitives":[{"attributes":{"POSITION":0}}]}],
             "buffers":[{"byteLength":36}],"bufferViews":[{"buffer":0,"byteLength":36}],
             "accessors":[{"bufferView":0,"componentType":5126,"count":3,"type":"VEC3"}]}
            """;
        var glb = BuildGlbFromJson(json);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var act = () => SceneGlbAssetReader.Read(
            glb,
            SceneAffineTransform.Identity,
            "Y",
            _ => throw new InvalidOperationException(),
            CancellationToken.None);

        act.Should().Throw<InvalidDataException>()
            .WithMessage("*Invalid glTF node graph*");
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
    }

    private static byte[] BuildStandardYUpGlb() => BuildLegacyGlb("{\"honuaUpAxis\":\"Y\"}");

    private static byte[] BuildLegacyGlb(string? extrasJson = null)
    {
        var extras = extrasJson is null ? "" : "\"extras\":" + extrasJson + ",";
        var json = """
            {"asset":{"version":"2.0",EXTRAS"generator":"Honua"},
             "scenes":[{"nodes":[0]}],"nodes":[{"mesh":0}],
             "meshes":[{"primitives":[{"attributes":{"POSITION":0}}]}],
             "buffers":[{"byteLength":36}],"bufferViews":[{"buffer":0,"byteLength":36}],
             "accessors":[{"bufferView":0,"componentType":5126,"count":3,"type":"VEC3"}],
             "extensions":{"EXT_structural_metadata":{
                 "schema":{"id":"honua_scene_schema","classes":{"feature":{"properties":{}}}},
                 "propertyTables":[{"class":"feature","count":1,"properties":{}}]}}}
            """.Replace("EXTRAS", extras, StringComparison.Ordinal);
        return BuildGlbFromJson(json);
    }

    private static byte[] BuildGlbFromJson(string json)
    {
        var jsonBytes = Encoding.UTF8.GetBytes(json.PadRight((json.Length + 3) / 4 * 4));
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(0x46546c67u);
        writer.Write(2u);
        writer.Write(28 + jsonBytes.Length + 36);
        writer.Write(jsonBytes.Length);
        writer.Write(0x4e4f534au);
        writer.Write(jsonBytes);
        writer.Write(36);
        writer.Write(0x004e4942u);
        foreach (var coordinate in new float[] { 1, 2, 3, 2, 2, 3, 1, 3, 3 })
        {
            writer.Write(coordinate);
        }
        return stream.ToArray();
    }
}
