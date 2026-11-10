#!/usr/bin/env python3
"""Measure an exported serving image and enforce its store/format boundary."""

import argparse
import hashlib
import re
import json
import os
from itertools import chain
from pathlib import Path
import subprocess
import tarfile

STORE_FILES = (
    'Honua.DuckDB.dll', 'DuckDB.NET.Data.dll', 'DuckDB.NET.Bindings.dll', 'libduckdb.so',
    'Honua.SqlServer.dll', 'Microsoft.Data.SqlClient.dll',
    'Microsoft.SqlServer.Server.dll', 'System.Configuration.ConfigurationManager.dll',
    'Honua.MySql.dll', 'MySqlConnector.dll',
    'Honua.Oracle.dll', 'Oracle.ManagedDataAccess.dll',
    'System.Diagnostics.PerformanceCounter.dll', 'System.DirectoryServices.Protocols.dll',
    'Honua.Redshift.dll', 'Honua.Snowflake.dll', 'Snowflake.Data.dll',
    'Google.Api.Gax.dll', 'Google.Api.Gax.Rest.dll', 'Google.Apis.dll', 'Google.Apis.Auth.dll',
    'Google.Apis.Core.dll', 'Google.Apis.Storage.v1.dll', 'Google.Cloud.Storage.V1.dll',
    'Mono.Unix.dll', 'libMono.Unix.so', 'libsf_mini_core.so',
    'System.CodeDom.dll', 'System.Management.dll', 'TimeZoneConverter.dll', 'Tomlyn.Signed.dll',
    'Honua.Databricks.dll', 'Honua.GeoServicesRest.dll',
)
FORMAT_FILES = (
    'app/ParquetSharpNative.so', 'app/libe_sqlite3.so', 'app/libSkiaSharp.so',
)


DEFAULT_IMAGE_MAX_BYTES = 220_000_000


def verify_size(size, profile):
    if profile == 'postgis' and size > DEFAULT_IMAGE_MAX_BYTES:
        raise ValueError(f'Default image exceeds {DEFAULT_IMAGE_MAX_BYTES} uncompressed bytes: {size}')


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


def manifest_assemblies(manifest):
    libraries = chain.from_iterable(target.values() for target in manifest.get('targets', {}).values())
    return {Path(name).name for library in libraries
            for group in ('runtime', 'native', 'runtimeTargets') for name in library.get(group, {})}


def verify_build_inputs(evidence, profile):
    names = manifest_assemblies(evidence['assets'])
    if evidence['compilation'] == 'jit':
        if not evidence.get('deps'):
            raise ValueError('Missing JIT runtime dependency manifest')
        names.update(manifest_assemblies(evidence['deps']))
    elif evidence['compilation'] == 'aot':
        responses = '\n'.join(evidence['responses']).lower()
        if 'honua.server.dll' not in responses or '-r:' not in responses:
            raise ValueError('Missing native compiler response evidence')
        names.update(name for name in STORE_FILES if name.lower() in responses)
    else:
        raise ValueError('Unknown image compilation mode')
    if profile == 'postgis':
        excluded = sorted(names & set(STORE_FILES))
        if excluded:
            raise ValueError('Excluded store compiler/runtime inputs: ' + ', '.join(excluded))


def oci_blob(archive, descriptor):
    digest = descriptor['digest']
    if not re.fullmatch(r'sha256:[a-f0-9]{64}', digest):
        raise ValueError('Unsupported OCI blob digest')
    member = archive.getmember('blobs/sha256/' + digest.split(':')[1])
    if not member.isfile() or member.size != descriptor['size']:
        raise ValueError('OCI blob size or type mismatch')
    payload = archive.extractfile(member).read()
    if hashlib.sha256(payload).hexdigest() != digest.split(':')[1]:
        raise ValueError('OCI blob checksum mismatch')
    return payload


def measure_oci_archive(path, image_id):
    with tarfile.open(path) as archive:
        pending = json.load(archive.extractfile('index.json'))['manifests']
        while pending:
            manifest_payload = oci_blob(archive, pending.pop())
            manifest = json.loads(manifest_payload)
            if 'manifests' in manifest:
                pending.extend(manifest['manifests'])
                continue
            if manifest.get('config', {}).get('digest') != image_id:
                continue
            oci_blob(archive, manifest['config'])
            for layer in manifest['layers']:
                if not layer.get('mediaType', '').endswith(('+gzip', '+zstd', '.gzip')):
                    raise ValueError('OCI size evidence must contain compressed layers')
                oci_blob(archive, layer)
            return compressed_manifest_measurement(manifest_payload, image_id)
    raise ValueError('OCI archive does not contain the loaded image')


def compressed_manifest_measurement(payload, image_id):
    manifest = json.loads(payload)
    if manifest.get('config', {}).get('digest') != image_id:
        raise ValueError('Manifest does not contain the loaded image')
    layers = manifest['layers']
    if not layers or any(not layer.get('mediaType', '').endswith(('+gzip', '+zstd', '.gzip'))
                         for layer in layers):
        raise ValueError('Size evidence must contain compressed layers')
    return {'config_digest': image_id,
            'compressed_layer_bytes': sum(layer['size'] for layer in layers),
            'config_bytes': manifest['config']['size'],
            'manifest_bytes': len(payload),
            'layer_media_types': sorted({layer['mediaType'] for layer in layers})}


def measure_registry_image(reference, image_id):
    repository, separator, digest = reference.partition('@')
    if not separator or not re.fullmatch(r'sha256:[a-f0-9]{64}', digest):
        raise ValueError('Registry measurement requires an immutable image digest')
    pending = [(digest, None)]
    visited = set()
    while pending:
        digest, expected_size = pending.pop()
        if digest in visited:
            continue
        visited.add(digest)
        if not re.fullmatch(r'sha256:[a-f0-9]{64}', digest):
            raise ValueError('Unsupported registry manifest digest')
        payload = subprocess.check_output(['docker', 'buildx', 'imagetools', 'inspect',
                                           '--raw', repository + '@' + digest])
        if hashlib.sha256(payload).hexdigest() != digest.split(':')[1]:
            raise ValueError('Registry manifest checksum mismatch')
        if expected_size is not None and len(payload) != expected_size:
            raise ValueError('Registry manifest size mismatch')
        manifest = json.loads(payload)
        if 'manifests' in manifest:
            pending.extend((child['digest'], child['size']) for child in manifest['manifests'])
        elif manifest.get('config', {}).get('digest') == image_id:
            return compressed_manifest_measurement(payload, image_id)
    raise ValueError('Registry manifest does not contain the loaded image')


def inspect_image(reference):
    return json.loads(subprocess.check_output(['docker', 'image', 'inspect', reference]))[0]


def exported_files(reference):
    container = subprocess.check_output(['docker', 'create', reference], text=True).strip()
    try:
        with subprocess.Popen(['docker', 'export', container], stdout=subprocess.PIPE) as export:
            with tarfile.open(fileobj=export.stdout, mode='r|') as archive:
                files = {}
                deps = None
                for member in archive:
                    if member.isfile():
                        files[member.name] = member.size
                        if member.name == 'app/Honua.Server.deps.json':
                            deps = json.load(archive.extractfile(member))
            if export.wait() != 0:
                raise RuntimeError('Image export failed')
        return files, deps
    finally:
        subprocess.run(['docker', 'rm', container], check=True, stdout=subprocess.DEVNULL)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('image')
    parser.add_argument('--profile', choices=['postgis', 'full'], required=True)
    parser.add_argument('--baseline')
    parser.add_argument('--compilation', choices=['jit', 'aot'])
    parser.add_argument('--evidence-directory', type=Path)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--oci-archive', type=Path)
    parser.add_argument('--published', action='store_true')
    parser.add_argument('--default-image', action='store_true')
    args = parser.parse_args()
    if not args.published and not all((args.baseline, args.compilation, args.evidence_directory)):
        parser.error('Export measurement requires baseline, compilation and evidence directory')
    inspection = inspect_image(args.image)
    if args.published:
        report = {'image': args.image, 'image_id': inspection['Id'], 'bytes': inspection['Size'],
                  'profile': args.profile, 'tags': os.environ.get('IMAGE_TAGS', '').splitlines(),
                  'oci': measure_registry_image(args.image, inspection['Id'])}
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(report, indent=2) + '\n')
        if args.default_image:
            verify_size(inspection['Size'], args.profile)
        print(f"{args.profile}: {inspection['Size']} uncompressed bytes; "
              f"{report['oci']['compressed_layer_bytes']} compressed layer bytes")
        return
    baseline = inspect_image(args.baseline)
    files, deps = exported_files(args.image)
    evidence = {
        'assets': json.loads((args.evidence_directory / 'project.assets.json').read_text()),
        'responses': [path.read_text() for path in args.evidence_directory.glob('*.ilc.rsp')],
        'deps': deps,
        'compilation': args.compilation,
    }
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
    if args.oci_archive:
        report['oci'] = measure_oci_archive(args.oci_archive, inspection['Id'])
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2) + '\n')
    if args.compilation == 'aot' or args.default_image:
        verify_size(inspection['Size'], args.profile)
    verify_files(files, args.profile)
    verify_build_inputs(evidence, args.profile)
    print(f"{args.profile}: {report['bytes']} bytes; saved {report['saved_bytes']} bytes")


if __name__ == '__main__':
    main()
