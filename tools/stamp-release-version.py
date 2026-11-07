#!/usr/bin/env python3
"""Stamp the project and loader manifest together in the CI checkout."""
from pathlib import Path
import re
import sys


def stamp(root, version):
    if not re.fullmatch(r'[1-9][0-9]*\.[1-9][0-9]*\.0(?:-beta)?', version):
        raise ValueError('Expected MAJOR.RUN.0 or MAJOR.RUN.0-beta')
    replacements = []
    for name, pattern, replacement in (
        ('WobblyWindows.csproj', r'<Version>[^<]+</Version>', f'<Version>{version}</Version>'),
        ('mod.toml', r'(?m)^version\s*=\s*"[^"\n]+"\s*$', f'version = "{version}"'),
    ):
        path = root / name
        contents, count = re.subn(pattern, replacement, path.read_text())
        if count != 1:
            raise ValueError(f'{name}: expected exactly one version, found {count}')
        replacements.append((path, contents))
    for path, contents in replacements:
        path.write_text(contents)


if __name__ == '__main__':
    stamp(Path(__file__).resolve().parents[1], sys.argv[1])
