// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Xml;

namespace Honua.Core.Features.Infrastructure.Validation;

public sealed class BoundedDepthXmlReader : XmlReader
{
    private readonly XmlReader _inner;
    private readonly int _maxDepth;

    public BoundedDepthXmlReader(XmlReader inner, int maxDepth)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDepth, 1);
        _inner = inner;
        _maxDepth = maxDepth;
    }

    public override XmlNodeType NodeType => _inner.NodeType;

    public override string LocalName => _inner.LocalName;

    public override string NamespaceURI => _inner.NamespaceURI;

    public override string Prefix => _inner.Prefix;

    public override string Value => _inner.Value;

    public override int Depth => _inner.Depth;

    public override string BaseURI => _inner.BaseURI;

    public override bool IsEmptyElement => _inner.IsEmptyElement;

    public override int AttributeCount => _inner.AttributeCount;

    public override bool EOF => _inner.EOF;

    public override ReadState ReadState => _inner.ReadState;

    public override XmlNameTable NameTable => _inner.NameTable;

    public override XmlReaderSettings? Settings => _inner.Settings;

    public override string Name => _inner.Name;

    public override bool HasValue => _inner.HasValue;

    public override bool IsDefault => _inner.IsDefault;

    public override char QuoteChar => _inner.QuoteChar;

    public override XmlSpace XmlSpace => _inner.XmlSpace;

    public override string XmlLang => _inner.XmlLang;

    public override bool CanResolveEntity => _inner.CanResolveEntity;

    public override string? GetAttribute(string name) => _inner.GetAttribute(name);

    public override string? GetAttribute(string name, string? namespaceURI) => _inner.GetAttribute(name, namespaceURI);

    public override string GetAttribute(int i) => _inner.GetAttribute(i);

    public override bool MoveToAttribute(string name) => _inner.MoveToAttribute(name);

    public override bool MoveToAttribute(string name, string? ns) => _inner.MoveToAttribute(name, ns);

    public override bool MoveToFirstAttribute() => _inner.MoveToFirstAttribute();

    public override bool MoveToNextAttribute() => _inner.MoveToNextAttribute();

    public override bool MoveToElement() => _inner.MoveToElement();

    public override bool ReadAttributeValue() => _inner.ReadAttributeValue();

    public override string? LookupNamespace(string prefix) => _inner.LookupNamespace(prefix);

    public override void ResolveEntity() => _inner.ResolveEntity();

    public override bool Read()
    {
        if (!_inner.Read())
        {
            return false;
        }

        EnsureDepthWithinLimit();
        return true;
    }

    public override async Task<bool> ReadAsync()
    {
        if (!await _inner.ReadAsync().ConfigureAwait(false))
        {
            return false;
        }

        EnsureDepthWithinLimit();
        return true;
    }

    public override Task<string> GetValueAsync() => _inner.GetValueAsync();

    public override void Close()
    {
        _inner.Close();
        _inner.Dispose();
    }

    private void EnsureDepthWithinLimit()
    {
        if (_inner.Depth >= _maxDepth)
        {
            throw new XmlException($"XML element nesting depth exceeds the maximum allowed depth of {_maxDepth}.");
        }
    }
}
