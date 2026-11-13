"""Prepare the private schema-140 fixture with current advisory warning policy."""
from pathlib import Path
import sys


def cap_schema(context):
    migrations = Path(context) / 'src/Honua.Server/Migrations'
    scripts = [(int(path.name.split('_', 1)[0]), path)
               for path in migrations.glob('[0-9]*_*.sql')]
    if not any(version == 140 for version, _ in scripts):
        raise ValueError('Lifecycle prior source must contain schema 140')
    for version, path in scripts:
        if version > 140:
            path.unlink()


def apply_advisory_policy(context):
    props = Path(context) / 'Directory.Build.props'
    content = props.read_text()
    if content.count('</Project>') != 1:
        raise ValueError('Lifecycle prior must contain one Directory.Build.props project')
    policy = ('  <PropertyGroup>\n'
              '    <WarningsNotAsErrors>$(WarningsNotAsErrors);NU1901;NU1902;NU1903;NU1904</WarningsNotAsErrors>\n'
              '  </PropertyGroup>\n')
    props.write_text(content.replace('</Project>', policy + '</Project>'))


if __name__ == '__main__':
    cap_schema(sys.argv[1])
    apply_advisory_policy(sys.argv[1])
