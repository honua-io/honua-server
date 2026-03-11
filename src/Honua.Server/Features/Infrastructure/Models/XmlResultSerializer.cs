// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Xml;
using System.Xml.Serialization;

namespace Honua.Server.Features.Infrastructure.Models;

internal static class XmlResultSerializer
{
    // XML serialization is isolated here so protocol endpoints keep their reflection-aware
    // implementation detail in one place.
    [RequiresDynamicCode("XmlSerializer generates runtime code for XML contract types.")]
    [RequiresUnreferencedCode("XmlSerializer requires all serialized XML contract members to be preserved.")]
    internal static string Serialize<T>(T value) where T : class
    {
        var serializer = new XmlSerializer(typeof(T));
        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            NewLineChars = "\n",
            Encoding = Encoding.UTF8,
            OmitXmlDeclaration = false
        };

        using var stringWriter = new Utf8StringWriter();
        using var xmlWriter = XmlWriter.Create(stringWriter, settings);
        serializer.Serialize(xmlWriter, value);
        return stringWriter.ToString();
    }

    private sealed class Utf8StringWriter : StringWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
    }
}
