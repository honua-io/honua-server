// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Xml;
using System.Xml.Linq;

namespace Honua.Protocols.GeoServices.Soap;

/// <summary>
/// Loads bounded SOAP XML without allowing an attacker-controlled element tree to grow
/// beyond the supported nesting depth.
/// </summary>
internal static class SoapXmlDocumentReader
{
    internal const int MaxElementDepth = 64;

    internal static async Task<XDocument> LoadAsync(
        Stream stream,
        long maxCharactersInDocument,
        CancellationToken cancellationToken)
    {
        var settings = new XmlReaderSettings
        {
            Async = true,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = maxCharactersInDocument
        };

        using var sourceReader = XmlReader.Create(stream, settings);
        using var depthReader = new DepthLimitingXmlReader(sourceReader, MaxElementDepth);
        return await XDocument.LoadAsync(depthReader, LoadOptions.None, cancellationToken).ConfigureAwait(false);
    }

    private sealed class DepthLimitingXmlReader(XmlReader source, int maxElementDepth) : XmlReader
    {
        public override int AttributeCount => source.AttributeCount;

        public override string BaseURI => source.BaseURI;

        public override int Depth => source.Depth;

        public override bool EOF => source.EOF;

        public override bool HasValue => source.HasValue;

        public override bool IsEmptyElement => source.IsEmptyElement;

        public override string LocalName => source.LocalName;

        public override string NamespaceURI => source.NamespaceURI;

        public override XmlNameTable NameTable => source.NameTable;

        public override XmlNodeType NodeType => source.NodeType;

        public override string Prefix => source.Prefix;

        public override ReadState ReadState => source.ReadState;

        public override string Value => source.Value;

        public override Task<string> GetValueAsync() => source.GetValueAsync();

        public override string GetAttribute(int i) => source.GetAttribute(i);

        public override string? GetAttribute(string name) => source.GetAttribute(name);

        public override string? GetAttribute(string name, string? namespaceURI)
            => source.GetAttribute(name, namespaceURI);

        public override string? LookupNamespace(string prefix) => source.LookupNamespace(prefix);

        public override bool MoveToAttribute(string name) => source.MoveToAttribute(name);

        public override bool MoveToAttribute(string name, string? ns) => source.MoveToAttribute(name, ns);

        public override void MoveToAttribute(int i) => source.MoveToAttribute(i);

        public override bool MoveToElement() => source.MoveToElement();

        public override bool MoveToFirstAttribute() => source.MoveToFirstAttribute();

        public override bool MoveToNextAttribute() => source.MoveToNextAttribute();

        public override bool Read()
        {
            var read = source.Read();
            EnsureDepthWithinLimit(read);
            return read;
        }

        public override bool ReadAttributeValue() => source.ReadAttributeValue();

        public override async Task<bool> ReadAsync()
        {
            var read = await source.ReadAsync().ConfigureAwait(false);
            EnsureDepthWithinLimit(read);
            return read;
        }

        public override void ResolveEntity() => source.ResolveEntity();

        public override void Close() => source.Close();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                source.Dispose();
            }

            base.Dispose(disposing);
        }

        private void EnsureDepthWithinLimit(bool read)
        {
            if (read && source.NodeType == XmlNodeType.Element && source.Depth > maxElementDepth)
            {
                throw new XmlException(
                    $"SOAP document exceeds the maximum element nesting depth of {maxElementDepth}.");
            }
        }
    }
}
