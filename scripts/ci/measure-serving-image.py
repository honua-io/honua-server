#!/usr/bin/env python3
"""Measure an exported serving image and enforce its store/format boundary."""

import argparse
import hashlib
import gzip
import io
import re
import json
import os
from itertools import chain
from pathlib import Path
import subprocess
import tarfile
import tempfile

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


IMAGE_SIZE_BUDGETS = {'rc': 370_000_000, 'ga': 220_000_000}


def verify_size(size, profile, release_stage='ga'):
    budget = IMAGE_SIZE_BUDGETS[release_stage]
    if profile == 'postgis' and size > budget:
        raise ValueError(f'Default image exceeds {budget} uncompressed bytes ({release_stage}): {size}')


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


def manifest_matches_image(payload, image_id):
    return (json.loads(payload).get('config', {}).get('digest') == image_id
            or 'sha256:' + hashlib.sha256(payload).hexdigest() == image_id)


def oci_image_payload(archive, image_id):
    pending = json.load(archive.extractfile('index.json'))['manifests']
    while pending:
        payload = oci_blob(archive, pending.pop())
        manifest = json.loads(payload)
        if 'manifests' in manifest:
            pending.extend(manifest['manifests'])
        elif manifest_matches_image(payload, image_id):
            return payload
    raise ValueError('OCI archive does not contain the loaded image')


def uncompressed_oci_bytes(archive, payload):
    manifest = json.loads(payload)
    config = json.loads(oci_blob(archive, manifest['config']))
    diff_ids = image_diff_ids(config, len(manifest['layers']))
    size = 0
    for layer, diff_id in zip(manifest['layers'], diff_ids):
        compressed = io.BytesIO(oci_blob(archive, layer))
        media_type = layer.get('mediaType', '')
        if media_type.endswith(('+gzip', '.gzip')):
            with gzip.GzipFile(fileobj=compressed) as stream:
                size += verified_layer_bytes(stream, diff_id)
        elif media_type in ('application/vnd.oci.image.layer.v1.tar',
                            'application/vnd.docker.image.rootfs.diff.tar'):
            size += verified_layer_bytes(compressed, diff_id)
        else:
            raise ValueError('Unsupported layer compression for uncompressed measurement')
    return size


def measure_oci_archive(path, image_id):
    with tarfile.open(path) as archive:
        payload = oci_image_payload(archive, image_id)
        return {**compressed_manifest_measurement(payload, image_id),
                'uncompressed_layer_bytes': uncompressed_oci_bytes(archive, payload)}


def image_diff_ids(config, layer_count):
    rootfs = config.get('rootfs', {})
    diff_ids = rootfs.get('diff_ids', [])
    if rootfs.get('type') != 'layers' or not diff_ids or len(diff_ids) != layer_count:
        raise ValueError('Image config has missing or mismatched layer diff IDs')
    return diff_ids


def verified_layer_bytes(stream, diff_id):
    digest = hashlib.sha256()
    size = 0
    for chunk in iter(lambda: stream.read(1024 * 1024), b''):
        digest.update(chunk)
        size += len(chunk)
    if 'sha256:' + digest.hexdigest() != diff_id:
        raise ValueError('Uncompressed layer diff ID mismatch')
    return size


def saved_image_bytes(path, image_id):
    with tarfile.open(path) as archive:
        if 'index.json' in archive.getnames():
            return uncompressed_oci_bytes(archive, oci_image_payload(archive, image_id))
        manifests = json.load(archive.extractfile('manifest.json'))
        for manifest in manifests:
            payload = archive.extractfile(manifest['Config']).read()
            if 'sha256:' + hashlib.sha256(payload).hexdigest() != image_id:
                continue
            diff_ids = image_diff_ids(json.loads(payload), len(manifest['Layers']))
            return sum(verified_layer_bytes(archive.extractfile(layer), diff_id)
                       for layer, diff_id in zip(manifest['Layers'], diff_ids))
    raise ValueError('Saved archive does not contain the loaded image')


def measure_saved_image(reference, image_id):
    with tempfile.TemporaryDirectory(prefix='honua-image-size-') as directory:
        path = Path(directory) / 'image.tar'
        subprocess.run(['docker', 'image', 'save', '--output', str(path), reference], check=True)
        return saved_image_bytes(path, image_id)


def compressed_manifest_measurement(payload, image_id):
    manifest = json.loads(payload)
    if not manifest_matches_image(payload, image_id):
        raise ValueError('Manifest does not contain the loaded image')
    layers = manifest['layers']
    if not layers or any(not layer.get('mediaType', '').endswith(('+gzip', '+zstd', '.gzip'))
                         for layer in layers):
        raise ValueError('Size evidence must contain compressed layers')
    return {'config_digest': manifest['config']['digest'],
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
        elif manifest_matches_image(payload, image_id):
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
    parser.add_argument('--release-stage', choices=IMAGE_SIZE_BUDGETS, default='ga')
    parser.add_argument('--baseline')
    parser.add_argument('--compilation', choices=['jit', 'aot'])
    parser.add_argument('--evidence-directory', type=Path)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--oci-archive', type=Path)
    parser.add_argument('--published', action='store_true')
    parser.add_argument('--default-image', action='store_true')
    parser.add_argument('--verify-jit-boundary', action='store_true')
    args = parser.parse_args()
    if not args.published and not all((args.baseline, args.compilation, args.evidence_directory)):
        parser.error('Export measurement requires baseline, compilation and evidence directory')
    if args.verify_jit_boundary and not args.published:
        parser.error('Published JIT boundary verification requires --published')
    inspection = inspect_image(args.image)
    if args.published:
        measure_published(args, inspection)
    else:
        measure_export(args, inspection)


def measure_published(args, inspection):
    size = measure_saved_image(args.image, inspection['Id'])
    report = {'image': args.image, 'image_id': inspection['Id'], 'bytes': size,
              'docker_storage_bytes': inspection['Size'],
              'size_method': 'verified-uncompressed-layer-bytes',
              'release_stage': args.release_stage,
              'size_budget_bytes': IMAGE_SIZE_BUDGETS[args.release_stage],
              'profile': args.profile, 'tags': os.environ.get('IMAGE_TAGS', '').splitlines(),
              'oci': measure_registry_image(args.image, inspection['Id'])}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2) + '\n')
    if args.default_image:
        verify_size(report['bytes'], args.profile, args.release_stage)
    if args.verify_jit_boundary:
        files, deps = exported_files(args.image)
        verify_files(files, args.profile)
        verify_build_inputs({'assets': {'targets': {}}, 'responses': [],
                             'deps': deps, 'compilation': 'jit'}, args.profile)
    print(f"{args.profile}: {size} uncompressed bytes; "
          f"{report['oci']['compressed_layer_bytes']} compressed layer bytes")


def measure_export(args, inspection):
    baseline = inspect_image(args.baseline)
    baseline_size = measure_saved_image(args.baseline, baseline['Id'])
    oci = measure_oci_archive(args.oci_archive, inspection['Id']) if args.oci_archive else None
    size = oci['uncompressed_layer_bytes'] if oci else measure_saved_image(args.image, inspection['Id'])
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
        'release_stage': args.release_stage,
        'size_budget_bytes': IMAGE_SIZE_BUDGETS[args.release_stage],
        'bytes': size,
        'docker_storage_bytes': inspection['Size'],
        'size_method': 'verified-uncompressed-layer-bytes',
        'app_bytes': sum(app_files.values()),
        'baseline_image': args.baseline,
        'baseline_bytes': baseline_size,
        'baseline_docker_storage_bytes': baseline['Size'],
        'saved_bytes': baseline_size - size,
        'files': app_files,
    }
    if oci:
        report['oci'] = oci
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2) + '\n')
    if args.compilation == 'aot' or args.default_image:
        verify_size(report['bytes'], args.profile, args.release_stage)
    verify_files(files, args.profile)
    verify_build_inputs(evidence, args.profile)
    print(f"{args.profile}: {report['bytes']} bytes; saved {report['saved_bytes']} bytes")


if __name__ == '__main__':
    main()
