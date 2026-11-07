#!/usr/bin/env python3
"""Select older rolling releases from all pages of GitHub API JSON."""
import argparse
import json
import re
import sys


def matches_channel(release, channel):
    suffix = '' if channel == 'main' else '-beta'
    # Include every manually selected major in the same rolling channel pool.
    return (bool(release.get('prerelease')) == (channel == 'beta')
            and re.fullmatch(r'v[1-9][0-9]*\.[1-9][0-9]*\.0' + suffix,
                             release['tag_name']) is not None)


def stale_tags(pages, channel, keep):
    if channel not in ('main', 'beta') or keep < 1:
        raise ValueError('Channel must be main or beta and retention must be positive')
    releases = [release for page in pages for release in page
                if not release.get('draft') and matches_channel(release, channel)]
    releases.sort(key=lambda release: (release['published_at'], release['id']), reverse=True)
    return [release['tag_name'] for release in releases[keep:]]


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--channel', choices=['main', 'beta'], required=True)
    parser.add_argument('--keep', type=int, required=True)
    args = parser.parse_args()
    for tag in stale_tags(json.load(sys.stdin), args.channel, args.keep):
        print(tag)
