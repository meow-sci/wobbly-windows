#!/usr/bin/env python3
"""Validate the exact mod payload and reject host-owned runtime dependencies."""
import json
from pathlib import Path
import sys
import tomllib
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
PAYLOAD = {'WobblyWindows.dll', 'WobblyWindows.deps.json', 'mod.toml', 'LICENSE', 'README.md'}


def check(stage, root=ROOT):
    files = list(stage.rglob('*'))
    actual = {path.relative_to(stage).as_posix() for path in files if path.is_file()}
    if actual != PAYLOAD or any(path.is_symlink() for path in files):
        raise ValueError(f'Unexpected distribution inventory: {sorted(actual)}')
    if any(path.stat().st_size == 0 for path in files if path.is_file()):
        raise ValueError('Distribution contains an empty file')
    project = ET.parse(root / 'WobblyWindows.csproj').getroot()
    version = project.findtext('./PropertyGroup/Version')
    manifest = tomllib.loads((stage / 'mod.toml').read_text())
    source_manifest = tomllib.loads((root / 'mod.toml').read_text())
    if (manifest != source_manifest or manifest.get('name') != 'wobbly-windows'
            or manifest.get('version') != version
            or manifest.get('StarMap', {}).get('EntryAssembly') != 'WobblyWindows'):
        raise ValueError('Staged manifest identity or version differs from source/project')
    for name in ('mod.toml', 'LICENSE', 'README.md'):
        if (stage / name).read_bytes() != (root / name).read_bytes():
            raise ValueError(f'Staged {name} differs from source')
    deps = json.loads((stage / 'WobblyWindows.deps.json').read_text())
    identity = f'WobblyWindows/{version}'
    if set(deps.get('libraries', {})) != {identity} or not deps.get('targets'):
        raise ValueError('Dependency manifest must contain only the versioned mod')
    for target in deps['targets'].values():
        if set(target) != {identity}:
            raise ValueError('Dependency manifest contains unexpected libraries')
        library = target[identity]
        if (set(library.get('runtime', {})) != {'WobblyWindows.dll'}
                or any(library.get(section) for section in ('dependencies', 'native', 'runtimeTargets', 'resources'))):
            raise ValueError('Dependency manifest contains unexpected runtime assets')


if __name__ == '__main__':
    check(Path(sys.argv[1]))
    print('PASS: distribution inventory, versions and host dependency exclusions')
