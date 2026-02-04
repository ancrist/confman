#!/usr/bin/env python3
"""
DotNext WAL Analyzer - Decode metadata and check for padding bugs.

Usage:
    python scripts/wal_analyzer.py <data-dir>

Example:
    python scripts/wal_analyzer.py src/Confman.Api/data-6100
"""

import struct
import sys
import os
from pathlib import Path
from datetime import datetime, timedelta

PAGE_SIZE = 16384  # 16KB

def read_metadata_entry(data: bytes, index: int) -> dict:
    """Parse a 64-byte metadata entry."""
    offset = index * 64
    if offset + 64 > len(data):
        return None

    entry = data[offset:offset + 64]

    # Unpack: term (long), timestamp (long), length (long), offset (ulong)
    term, timestamp_ticks, length, data_offset = struct.unpack('<qqQQ', entry[:32])

    # Check if entry is empty (all zeros or invalid)
    if term == 0 and length == 0 and data_offset == 0:
        return None

    # Convert .NET ticks to datetime (ticks since 0001-01-01)
    # 621355968000000000 = ticks from 0001-01-01 to 1970-01-01
    if timestamp_ticks > 0:
        unix_ticks = timestamp_ticks - 621355968000000000
        timestamp = datetime(1970, 1, 1) + timedelta(microseconds=unix_ticks // 10)
    else:
        timestamp = None

    return {
        'index': index,
        'term': term,
        'timestamp': timestamp,
        'length': length,
        'offset': data_offset,
        'end_offset': data_offset + length,
    }


def read_data_at_offset(data_dir: Path, offset: int, length: int) -> bytes:
    """Read data from the paged data files at the given offset."""
    page_index = offset // PAGE_SIZE
    page_offset = offset % PAGE_SIZE

    data_file = data_dir / 'raft-log' / 'data' / str(page_index)
    if not data_file.exists():
        return b''

    with open(data_file, 'rb') as f:
        f.seek(page_offset)
        # Read up to length bytes, but don't cross page boundary for simplicity
        read_len = min(length, PAGE_SIZE - page_offset)
        return f.read(read_len)


def analyze_entry(data_dir: Path, entry: dict) -> dict:
    """Analyze an entry for padding issues."""
    data = read_data_at_offset(data_dir, entry['offset'], min(entry['length'], 100))

    # Check if data starts with JSON
    starts_with_json = data.startswith(b'{"$type"')

    # Count leading null bytes
    null_count = 0
    for b in data:
        if b == 0:
            null_count += 1
        else:
            break

    # Check if this entry crosses a page boundary
    start_page = entry['offset'] // PAGE_SIZE
    end_page = (entry['offset'] + entry['length'] - 1) // PAGE_SIZE
    crosses_page = start_page != end_page

    # Calculate expected start if aligned to page
    page_boundary = (start_page + 1) * PAGE_SIZE
    remaining_in_page = page_boundary - entry['offset']

    return {
        **entry,
        'data_preview': data[:60].decode('utf-8', errors='replace') if starts_with_json else f"<{null_count} null bytes> then: {data[null_count:null_count+40].decode('utf-8', errors='replace')}",
        'starts_with_json': starts_with_json,
        'leading_nulls': null_count,
        'crosses_page': crosses_page,
        'has_padding_bug': null_count > 0 and not starts_with_json,
        'remaining_in_page': remaining_in_page if remaining_in_page < entry['length'] else None,
    }


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(1)

    data_dir = Path(sys.argv[1])
    metadata_file = data_dir / 'raft-log' / 'metadata' / '0'

    if not metadata_file.exists():
        print(f"Error: Metadata file not found: {metadata_file}")
        sys.exit(1)

    print(f"Analyzing WAL at: {data_dir}")
    print(f"Page size: {PAGE_SIZE} bytes")
    print("=" * 80)

    with open(metadata_file, 'rb') as f:
        metadata_data = f.read()

    max_entries = len(metadata_data) // 64
    print(f"Metadata file size: {len(metadata_data)} bytes ({max_entries} possible entries)")
    print()

    entries = []
    for i in range(max_entries):
        entry = read_metadata_entry(metadata_data, i)
        if entry:
            entries.append(entry)

    print(f"Found {len(entries)} entries")
    print()

    # Analyze each entry
    buggy_entries = []
    for entry in entries:
        analysis = analyze_entry(data_dir, entry)
        entries[entries.index(entry)] = analysis
        if analysis['has_padding_bug']:
            buggy_entries.append(analysis)

    # Show all entries
    print("Entry Details:")
    print("-" * 80)
    print(f"{'Idx':>4} {'Offset':>10} {'Length':>8} {'End':>10} {'Page':>4} {'Status':<10} Preview")
    print("-" * 80)

    for e in entries:
        page = e['offset'] // PAGE_SIZE
        status = "BUG!" if e['has_padding_bug'] else ("OK" if e['starts_with_json'] else "?")
        preview = e['data_preview'][:50] + "..." if len(e['data_preview']) > 50 else e['data_preview']
        print(f"{e['index']:>4} {e['offset']:>10} {e['length']:>8} {e['end_offset']:>10} {page:>4} {status:<10} {preview}")

    print()
    print("=" * 80)

    if buggy_entries:
        print(f"\n⚠️  PADDING BUG DETECTED in {len(buggy_entries)} entries:")
        print("-" * 80)
        for e in buggy_entries:
            print(f"\nEntry {e['index']}:")
            print(f"  Metadata offset: {e['offset']} (0x{e['offset']:x})")
            print(f"  Metadata length: {e['length']} (includes {e['leading_nulls']} bytes of padding)")
            print(f"  Actual JSON at:  {e['offset'] + e['leading_nulls']} (0x{e['offset'] + e['leading_nulls']:x})")
            print(f"  Page boundary:   {((e['offset'] // PAGE_SIZE) + 1) * PAGE_SIZE}")
            print(f"  Leading nulls:   {e['leading_nulls']} bytes")
            print(f"  Data preview:    {e['data_preview'][:80]}")
    else:
        print("\n✅ No padding bugs detected - all entries start with valid JSON")


if __name__ == '__main__':
    main()
