"""Resolve the assemblies a shard must execute, including grouped suites."""

DEFAULT_PROJECT = 'tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj'


def shard_projects(shard):
    if shard.get('csprojs') is None:
        return [shard.get('csproj') or DEFAULT_PROJECT]
    projects = shard['csprojs']
    if (shard.get('csproj') or not isinstance(projects, list) or not projects
            or any(not isinstance(p, str) or not p.endswith('.csproj') for p in projects)
            or len(projects) != len(set(projects))):
        raise ValueError('csprojs must contain distinct project paths and cannot accompany csproj')
    return projects
