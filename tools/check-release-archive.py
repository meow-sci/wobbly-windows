#!/usr/bin/env python3
"""Check the shipped ZIP against the validated stage and its SHA-256 sidecar."""
import hashlib
from pathlib import Path
import sys
import zipfile


def check(stage, archive):
    expected = {'wobbly-windows/' + path.relative_to(stage).as_posix(): path.read_bytes()
                for path in stage.rglob('*') if path.is_file()}
    if not expected:
        raise ValueError('Stage is empty')
    with zipfile.ZipFile(archive) as package:
        files = [item for item in package.infolist() if not item.is_dir()]
        if len(files) != len(expected) or {item.filename for item in files} != set(expected):
            raise ValueError('ZIP file list differs from stage (or contains duplicate entries)')
        for item in files:
            if package.read(item) != expected[item.filename]:
                raise ValueError(f'ZIP contents differ from stage: {item.filename}')
    checksum = Path(str(archive) + '.sha256').read_text().split()
    if checksum != [hashlib.sha256(archive.read_bytes()).hexdigest().upper(), archive.name]:
        raise ValueError('ZIP checksum or sidecar filename differs')


if __name__ == '__main__':
    check(Path(sys.argv[1]), Path(sys.argv[2]))
    print('PASS: release ZIP matches stage and checksum')
