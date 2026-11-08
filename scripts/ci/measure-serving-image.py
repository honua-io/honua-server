#!/usr/bin/env python3
"""Measure an exported serving image and enforce its store/format boundary."""

import argparse
import json
from pathlib import Path
import subprocess
import tarfile

STORE_FILES = (
    'Honua.DuckDB.dll', 'DuckDB.NET.Data.dll', 'DuckDB.NET.Bindings.dll', 'libduckdb.so',
    'Honua.SqlServer.dll', 'Microsoft.Data.SqlClient.dll',
    'Honua.MySql.dll', 'MySqlConnector.dll',
    'Honua.Oracle.dll', 'Oracle.ManagedDataAccess.dll',
    'Honua.Redshift.dll', 'Honua.Snowflake.dll', 'Snowflake.Data.dll',
    'Honua.Databricks.dll', 'Honua.GeoServicesRest.dll',
)
FORMAT_FILES = (
    'app/ParquetSharpNative.so', 'app/libe_sqlite3.so', 'app/libSkiaSharp.so',
)


def verify_files(files, profile):
    shell_files = {
        'bin/sh', 'bin/bash', 'bin/dash', 'usr/bin/sh', 'usr/bin/bash', 'usr/bin/dash',
        'usr/bin/apt', 'usr/bin/apt-get',
    }
    if shell_files & files.keys():
        raise ValueError('Runtime contains a shell or package manager')
    names = {Path(name).name for name in files if name.startswith('app/')}
    satellites = sorted(name for name in files if name.endswith('.resources.dll'))
    if satellites:
        raise ValueError('Unexpected satellite assemblies: ' + ', '.join(satellites))
    missing_formats = sorted(set(FORMAT_FILES) - files.keys())
    if missing_formats:
        raise ValueError('Missing format/render libraries: ' + ', '.join(missing_formats))
    if profile == 'postgis':
        excluded = sorted(names & set(STORE_FILES))
        if excluded:
            raise ValueError('Excluded store files in default image: ' + ', '.join(excluded))
    elif profile == 'full':
        missing = sorted(set(STORE_FILES) - names)
        if missing:
            raise ValueError('Missing full-image store files: ' + ', '.join(missing))
    else:
        raise ValueError('Unknown store profile: ' + profile)


def inspect_image(reference):
    return json.loads(subprocess.check_output(['docker', 'image', 'inspect', reference]))[0]


def exported_files(reference):
    container = subprocess.check_output(['docker', 'create', reference], text=True).strip()
    try:
        with subprocess.Popen(['docker', 'export', container], stdout=subprocess.PIPE) as export:
            with tarfile.open(fileobj=export.stdout, mode='r|') as archive:
                files = {member.name: member.size for member in archive if member.isfile()}
            if export.wait() != 0:
                raise RuntimeError('Image export failed')
        return files
    finally:
        subprocess.run(['docker', 'rm', container], check=True, stdout=subprocess.DEVNULL)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('image')
    parser.add_argument('--profile', choices=['postgis', 'full'], required=True)
    parser.add_argument('--baseline', required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    inspection = inspect_image(args.image)
    baseline = inspect_image(args.baseline)
    files = exported_files(args.image)
    app_files = {name: size for name, size in files.items() if name.startswith('app/')}
    report = {
        'image': args.image,
        'revision': inspection['Config'].get('Labels', {}).get('org.opencontainers.image.revision'),
        'profile': args.profile,
        'bytes': inspection['Size'],
        'app_bytes': sum(app_files.values()),
        'baseline_image': args.baseline,
        'baseline_bytes': baseline['Size'],
        'saved_bytes': baseline['Size'] - inspection['Size'],
        'files': app_files,
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2) + '\n')
    verify_files(files, args.profile)
    print(f"{args.profile}: {report['bytes']} bytes; saved {report['saved_bytes']} bytes")


if __name__ == '__main__':
    main()
