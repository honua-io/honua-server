// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Spec.Domain;

namespace Honua.Core.Features.Spec.Abstractions;

public interface ISpecCatalogSnapshot
{
    string Version { get; }

    TypeRef? ResolveSource(SourceBinding source);
}

public sealed class StaticSpecCatalogSnapshot : ISpecCatalogSnapshot
{
    private readonly IReadOnlyDictionary<string, TypeRef> _sources;

    public StaticSpecCatalogSnapshot(string version, IReadOnlyDictionary<string, TypeRef> sources)
    {
        Version = version ?? throw new ArgumentNullException(nameof(version));
        _sources = sources ?? throw new ArgumentNullException(nameof(sources));
    }

    public static ISpecCatalogSnapshot Empty { get; } = new StaticSpecCatalogSnapshot("empty", new Dictionary<string, TypeRef>(StringComparer.Ordinal));

    public string Version { get; }

    public TypeRef? ResolveSource(SourceBinding source)
        => _sources.TryGetValue(source.Id, out var type) ? type : null;
}
