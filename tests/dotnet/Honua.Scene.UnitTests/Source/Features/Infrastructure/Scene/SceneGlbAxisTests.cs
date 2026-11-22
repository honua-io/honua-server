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
        var meshes = SceneGlbAssetReader.Read(BuildGlb(standardYUp), SceneAffineTransform.Identity,
            upAxis, _ => throw new InvalidOperationException(), CancellationToken.None);

        var vertex = meshes.Single().Triangles.Single().A.Position;
        vertex.X.Should().Be(1);
        vertex.Y.Should().Be(expectedY);
        vertex.Z.Should().Be(expectedZ);
    }

    private static byte[] BuildGlb(bool standardYUp)
    {
        var extras = standardYUp ? "\"extras\":{\"honuaUpAxis\":\"Y\"}," : "";
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
