# SQLite Cache Design

## Location

The cache database is stored at:

```text
%LocalAppData%\FileGroupy\Cache\scan-cache.db
```

The cache is local to the Windows user. It never writes cache files into scanned folders, removable disks, or MTP/PTP devices.

## Responsibilities

`ScanCacheDbContext` and its entities describe the database schema and are used for schema creation, reads, and cache invalidation.

`SqliteScanCacheStore` keeps the public cache contract used by scanners and device services. It uses EF Core for structured reads and invalidation. It uses parameterized `Microsoft.Data.Sqlite` commands inside one transaction for large bulk writes, avoiding EF change-tracking overhead for large scans.

## Tables

| Table | Purpose | Primary key |
| --- | --- | --- |
| `scan_cache` | One cached scan snapshot and its counters | `cache_key` |
| `scan_files` | File index belonging to a scan snapshot | `cache_key`, `full_path` |
| `image_validation` | Cached raster-image validation result | source, path, size, modified time |

`cache_key` is derived from `source_kind`, `source_id`, and `root_path`. This prevents local disk and MTP/PTP paths from sharing cache entries.

## Read And Write Flow

1. The scanner requests a cache entry using source kind, source ID, root path, and a TTL.
2. A valid entry is read through EF Core with its file rows.
3. A miss or expired entry performs a real scan.
4. The result is stored with one SQLite transaction: old file rows are removed, scan metadata is upserted, and file rows are inserted using a reused parameterized command.
5. Cache failures are fail-open. The caller continues with real scan results or revalidates images.

## TTL Policy

| Source | Scan cache TTL | Image validation TTL |
| --- | --- | --- |
| Local disk or removable disk | 10 minutes | 24 hours |
| MTP/PTP device | 1 hour | 1 hour |

Local directory changes cannot be tracked perfectly without OS file-system watchers. The short local TTL intentionally trades a small stale-result window for faster repeated access to large folders.

## Recoverable Delete Object Store

SQLite stores only snapshots, original paths, ordered chunk manifests, object references, and transaction state. File content is stored under:

```text
%LocalAppData%\FileGroupy\Recovery\objects\<HashPrefix>\<BLAKE3 Hash>.zst|.raw
```

- FastCDC creates content-defined chunks targeting 1 MiB (256 KiB–4 MiB) in bounded 32 MiB segments.
- BLAKE3 addresses chunks; identical chunks are stored once.
- Zstandard level 3 compresses suitable chunks only when it saves at least 5%.
- Already compressed media, archives, Office Open XML, and PDF content is stored raw.
- Reference counts retain shared objects until the final manifest releases them.
- Restore streams chunks in order and verifies the final file size.
- Startup reconciliation repairs reference counts and removes unreferenced temporary/orphan objects.
- Legacy snapshots using physical `recovery_path` files remain restorable.

## Invalid Image Cache

Image validation entries are keyed by source kind, source ID, full path, size, and modification time. A changed file misses the cache and is validated again. SVG files are excluded because they are vector images and are not passed to the WPF bitmap decoder.

For MTP/PTP, validation runs sequentially per device session. This avoids repeated connect/disconnect operations and avoids unsafe concurrent device transfers.

## Invalidation

After a successful MTP/PTP copy, move, or delete, the cache store deletes scan metadata, file rows, and image validation rows for the affected device ID. Local scans use TTL refresh and do not attempt long-lived invalidation based only on root directory timestamps.