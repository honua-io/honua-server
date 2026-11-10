"""Cap the private lifecycle fixture at the schema required by the restore journey."""
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


if __name__ == '__main__':
    cap_schema(sys.argv[1])
