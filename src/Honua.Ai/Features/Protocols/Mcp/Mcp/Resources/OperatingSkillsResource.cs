// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Honua.Ai.Protocols.Mcp.Models;

namespace Honua.Ai.Protocols.Mcp.Resources;

// Immutable release content, embedded from the same files shipped in the quickstart.
internal sealed class OperatingSkillsResource : IMcpResource
{
    private const string ResourcePrefix = "Honua.Ai.Skills/";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    public static OperatingSkillsResource Instance { get; } = new();
    private readonly Dictionary<string, SkillFile> _files;
    public IReadOnlyList<McpSkill> Skills { get; }
    public string Family => "skills";

    private OperatingSkillsResource()
    {
        var assembly = typeof(OperatingSkillsResource).Assembly;
        _files = new Dictionary<string, SkillFile>(StringComparer.Ordinal);
        foreach (var name in assembly.GetManifestResourceNames()
                     .Where(static name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal)))
        {
            var path = name[ResourcePrefix.Length..];
            if (path.Split('/').Any(static part => part is "" or "." or "..") || path.Contains('%') || path.Contains('\\'))
                throw new InvalidOperationException("Embedded skill paths must be canonical.");
            using var source = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"Embedded skill file '{name}' is missing.");
            using var buffer = new MemoryStream();
            source.CopyTo(buffer);
            var bytes = buffer.ToArray();
            string? text = null;
            try { text = StrictUtf8.GetString(bytes); }
            catch (DecoderFallbackException) { /* Binary supporting files use base64. */ }
            var uri = "skill://" + path;
            _files.Add(uri, new SkillFile(new McpSkillResource(uri,
                "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.LongLength),
                text, text is null ? Convert.ToBase64String(bytes) : null));
        }

        Skills = _files.Values.Where(static file => file.Metadata.Uri.EndsWith("/SKILL.md", StringComparison.Ordinal))
            .OrderBy(static file => file.Metadata.Uri, StringComparer.Ordinal)
            .Select(CreateSkill).ToArray();
        if (Skills.Count == 0)
            throw new InvalidOperationException("No operating skills were embedded.");
    }

    private McpSkill CreateSkill(SkillFile entry)
    {
        var markdown = entry.Text ?? throw new InvalidOperationException("SKILL.md must be UTF-8.");
        var parts = markdown.Split("---", 3);
        if (parts.Length != 3 || parts[0].Length != 0)
            throw new InvalidOperationException("SKILL.md must start with frontmatter.");
        // JSON is a YAML subset. Canonical skills author JSON frontmatter so every field,
        // including unknown future metadata, survives without reflection or a YAML subset parser.
        using var document = JsonDocument.Parse(parts[1]);
        var frontmatter = document.RootElement;
        var root = entry.Metadata.Uri[..^"SKILL.md".Length];
        var directoryName = root.TrimEnd('/').Split('/')[^1];
        if (frontmatter.ValueKind != JsonValueKind.Object
            || !frontmatter.TryGetProperty("name", out var name) || name.GetString() != directoryName
            || !frontmatter.TryGetProperty("description", out var description)
            || description.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(description.GetString()))
            throw new InvalidOperationException("Skill frontmatter must name its directory and describe its purpose.");
        var resources = _files.Values.Where(file => file.Metadata.Uri.StartsWith(root, StringComparison.Ordinal))
            .Select(static file => file.Metadata).OrderBy(static file => file.Uri, StringComparer.Ordinal).ToArray();
        if (resources.Length > 512 || resources.Sum(static file => file.Size) > 16_777_216)
            throw new InvalidOperationException("An embedded skill exceeds the MCP skills extension limits.");
        return new McpSkill(entry.Metadata.Uri, frontmatter.Clone(), resources);
    }

    public IReadOnlyList<McpResourceDescriptor> Describe() => _files.Values
        .OrderBy(static file => file.Metadata.Uri, StringComparer.Ordinal)
        .Select(static file => new McpResourceDescriptor
        {
            Uri = file.Metadata.Uri,
            Name = file.Metadata.Uri["skill://".Length..],
            MimeType = file.Text is null ? "application/octet-stream" : "text/plain"
        }).ToArray();

    public IReadOnlyList<McpResourceTemplateDescriptor> DescribeTemplates() => [];
    public bool CanHandle(string uri) => _files.ContainsKey(uri);

    public Task<McpResourcesReadResult> ReadAsync(HttpContext httpContext, string uri, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var file = _files[uri];
        return Task.FromResult(new McpResourcesReadResult
        {
            Contents = [new McpResourceContent
            {
                Uri = uri, MimeType = file.Text is null ? "application/octet-stream" : "text/plain",
                Text = file.Text, Blob = file.Blob
            }]
        });
    }

    private sealed record SkillFile(McpSkillResource Metadata, string? Text, string? Blob);
}
