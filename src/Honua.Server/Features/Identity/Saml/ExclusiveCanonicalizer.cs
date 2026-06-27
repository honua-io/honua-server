// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Xml.Linq;

namespace Honua.Server.Features.Identity.Saml;

/// <summary>
/// Exclusive XML Canonicalization (Exclusive C14N, RFC 3741) for an <see cref="XElement"/>
/// subtree, without comments. Implemented over <see cref="System.Xml.Linq"/> so SAML signature
/// verification needs no dependency on <c>System.Security.Cryptography.Xml</c> (which is not in
/// the in-box shared framework and is not AOT/trim-friendly).
/// </summary>
/// <remarks>
/// Implements the rules that matter for SAML enveloped signatures: UTF-8 output, namespace
/// nodes rendered only when visibly utilized (or named in <c>InclusiveNamespaces</c>),
/// lexicographic ordering of namespaces (by prefix) and attributes (by namespace URI then
/// local name), <c>&amp;</c>/<c>&lt;</c>/<c>&gt;</c>/<c>"</c> escaping in attribute values and
/// <c>&amp;</c>/<c>&lt;</c>/<c>&gt;</c>/CR escaping in text, and empty elements rendered with an
/// explicit start/end tag pair.
/// </remarks>
internal static class ExclusiveCanonicalizer
{
    private static readonly XNamespace XmlNs = "http://www.w3.org/XML/1998/namespace";

    /// <summary>
    /// Canonicalizes <paramref name="element"/> (in the namespace context of its live ancestors)
    /// and returns the UTF-8 bytes.
    /// </summary>
    public static byte[] Canonicalize(XElement element, IReadOnlyList<string> inclusivePrefixes)
    {
        ArgumentNullException.ThrowIfNull(element);

        var builder = new StringBuilder(1024);
        var inclusive = new HashSet<string>(inclusivePrefixes ?? [], StringComparer.Ordinal);

        // Rendered-namespace context inherited from ancestors: maps prefix -> URI that has
        // already been output by an enclosing (already-rendered) element. For a detached or
        // top-level element this starts empty; Exclusive C14N intentionally does not inherit
        // unused ancestor namespaces.
        WriteElement(builder, element, new Dictionary<string, string>(StringComparer.Ordinal), inclusive);

        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    private static void WriteElement(
        StringBuilder builder,
        XElement element,
        Dictionary<string, string> renderedNamespaces,
        HashSet<string> inclusivePrefixes)
    {
        // Determine the namespaces this element must render. Under Exclusive C14N an element
        // renders a namespace node when (a) it is "visibly utilized" by the element name or one
        // of its attributes and not already rendered with the same value by an ancestor, or
        // (b) its prefix appears in the InclusiveNamespaces PrefixList.
        var localRendered = new Dictionary<string, string>(renderedNamespaces, StringComparer.Ordinal);
        var namespacesToOutput = new SortedDictionary<string, string>(PrefixComparer.Instance);

        // (a) the element's own namespace.
        ConsiderNamespace(element.Name, element, localRendered, namespacesToOutput);

        // (a) namespaces used by qualified attributes.
        foreach (var attribute in element.Attributes())
        {
            if (attribute.IsNamespaceDeclaration)
            {
                continue;
            }

            if (attribute.Name.Namespace != XNamespace.None)
            {
                ConsiderNamespace(attribute.Name, element, localRendered, namespacesToOutput);
            }
        }

        // (b) InclusiveNamespaces PrefixList entries that are in scope on this element.
        if (inclusivePrefixes.Count > 0)
        {
            foreach (var prefix in inclusivePrefixes)
            {
                var uri = ResolveInScopeNamespace(element, prefix);
                if (uri is null)
                {
                    continue;
                }

                if (!localRendered.TryGetValue(prefix, out var alreadyRendered) ||
                    !string.Equals(alreadyRendered, uri, StringComparison.Ordinal))
                {
                    namespacesToOutput[prefix] = uri;
                    localRendered[prefix] = uri;
                }
            }
        }

        var qualifiedName = QualifiedName(element.Name, element);

        builder.Append('<').Append(qualifiedName);

        // Namespace declarations first, ordered by prefix (default namespace, prefix ""), then
        // attributes ordered by (namespace URI, local name).
        foreach (var ns in namespacesToOutput)
        {
            if (ns.Key.Length == 0)
            {
                builder.Append(" xmlns=\"");
            }
            else
            {
                builder.Append(" xmlns:").Append(ns.Key).Append("=\"");
            }

            AppendEscapedAttributeValue(builder, ns.Value);
            builder.Append('"');
        }

        foreach (var attribute in element.Attributes()
                     .Where(a => !a.IsNamespaceDeclaration)
                     .OrderBy(a => a, AttributeComparer.Instance))
        {
            builder.Append(' ').Append(QualifiedAttributeName(attribute, element)).Append("=\"");
            AppendEscapedAttributeValue(builder, attribute.Value);
            builder.Append('"');
        }

        builder.Append('>');

        foreach (var node in element.Nodes())
        {
            switch (node)
            {
                case XElement childElement:
                    WriteElement(builder, childElement, localRendered, inclusivePrefixes);
                    break;
                case XText text:
                    AppendEscapedText(builder, text.Value);
                    break;
                // Comments are excluded (C14N without comments); processing instructions in
                // SAML assertions are not expected and are omitted.
                default:
                    break;
            }
        }

        builder.Append("</").Append(qualifiedName).Append('>');
    }

    private static void ConsiderNamespace(
        XName name,
        XElement scope,
        Dictionary<string, string> rendered,
        SortedDictionary<string, string> toOutput)
    {
        var uri = name.NamespaceName;

        // The XML namespace is implicitly declared and never rendered.
        if (string.Equals(uri, XmlNs.NamespaceName, StringComparison.Ordinal))
        {
            return;
        }

        // Resolve the real prefix from the element scope. XName does not carry the prefix, so a
        // prefixed name (e.g. a root <samlp:LogoutRequest>) must render xmlns:<prefix>, not the
        // default namespace — resolving via the scope is what distinguishes the two.
        var prefix = scope.GetPrefixOfNamespace(name.Namespace) ?? string.Empty;

        // Attributes in no namespace contribute nothing; an element in no namespace only needs
        // a default-namespace undeclaration if an ancestor rendered one (handled below).
        if (uri.Length == 0)
        {
            if (prefix.Length == 0 && rendered.TryGetValue(string.Empty, out var current) && current.Length != 0)
            {
                // Default namespace must be undeclared: xmlns="".
                toOutput[string.Empty] = string.Empty;
                rendered[string.Empty] = string.Empty;
            }

            return;
        }

        if (rendered.TryGetValue(prefix, out var renderedUri) && string.Equals(renderedUri, uri, StringComparison.Ordinal))
        {
            return;
        }

        toOutput[prefix] = uri;
        rendered[prefix] = uri;
    }

    private static string? ResolveInScopeNamespace(XElement element, string prefix)
    {
        var xname = prefix.Length == 0
            ? element.GetDefaultNamespace()
            : element.GetNamespaceOfPrefix(prefix);
        var uri = xname?.NamespaceName;
        return string.IsNullOrEmpty(uri) ? null : uri;
    }

    private static string QualifiedName(XName name, XElement scope)
    {
        if (name.Namespace == XNamespace.None)
        {
            return name.LocalName;
        }

        var prefix = scope.GetPrefixOfNamespace(name.Namespace);
        return string.IsNullOrEmpty(prefix) ? name.LocalName : prefix + ":" + name.LocalName;
    }

    private static string QualifiedAttributeName(XAttribute attribute, XElement scope)
    {
        if (attribute.Name.Namespace == XNamespace.None)
        {
            return attribute.Name.LocalName;
        }

        var prefix = scope.GetPrefixOfNamespace(attribute.Name.Namespace);
        return string.IsNullOrEmpty(prefix) ? attribute.Name.LocalName : prefix + ":" + attribute.Name.LocalName;
    }

    private static void AppendEscapedAttributeValue(StringBuilder builder, string value)
    {
        foreach (var c in value)
        {
            switch (c)
            {
                case '&': builder.Append("&amp;"); break;
                case '<': builder.Append("&lt;"); break;
                case '"': builder.Append("&quot;"); break;
                case '\t': builder.Append("&#x9;"); break;
                case '\n': builder.Append("&#xA;"); break;
                case '\r': builder.Append("&#xD;"); break;
                default: builder.Append(c); break;
            }
        }
    }

    private static void AppendEscapedText(StringBuilder builder, string value)
    {
        foreach (var c in value)
        {
            switch (c)
            {
                case '&': builder.Append("&amp;"); break;
                case '<': builder.Append("&lt;"); break;
                case '>': builder.Append("&gt;"); break;
                case '\r': builder.Append("&#xD;"); break;
                default: builder.Append(c); break;
            }
        }
    }

    /// <summary>Orders namespace declarations by prefix; the default namespace ("") sorts first.</summary>
    private sealed class PrefixComparer : IComparer<string>
    {
        public static readonly PrefixComparer Instance = new();

        public int Compare(string? x, string? y) => string.CompareOrdinal(x, y);
    }

    /// <summary>Orders attributes by namespace URI then local name (Exclusive C14N rule).</summary>
    private sealed class AttributeComparer : IComparer<XAttribute>
    {
        public static readonly AttributeComparer Instance = new();

        public int Compare(XAttribute? x, XAttribute? y)
        {
            if (x is null || y is null)
            {
                return Comparer<object?>.Default.Compare(x, y);
            }

            // Attributes in no namespace sort before namespaced attributes; otherwise by URI.
            var nsCompare = string.CompareOrdinal(x.Name.NamespaceName, y.Name.NamespaceName);
            return nsCompare != 0 ? nsCompare : string.CompareOrdinal(x.Name.LocalName, y.Name.LocalName);
        }
    }
}
