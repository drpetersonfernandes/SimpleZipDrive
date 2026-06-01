# FileBenchmark

A console tool for measuring cold-file I/O performance. Drops the Windows Standby List (page cache) between each test to ensure accurate, hardware-bound disk I/O measurements unaffected by OS-level caching.

## Requirements

- [.NET 10.0 Runtime](https://dotnet.microsoft.com/download)
- `xxhsum.exe` shipped alongside the executable (bundled automatically on build)
- `RAMMap64.exe` shipped alongside the executable (bundled automatically on build)
- **Administrator privileges** (optional, required only for cache clearing — see below)

## Usage

```
FileBenchmark <filepath> [--no-clear]
```

Or drag-and-drop a file onto `FileBenchmark.exe`.

Results are printed to the console and appended to `result.txt` in the executable's directory.

### Flags

| Flag | Description |
|------|-------------|
| `--no-clear` | Skip Windows Standby List purging between tests (allows warm-cache comparison) |

## Cache Clearing

By default, the Windows Standby List (page cache) is purged before **each** of the three benchmarks using RAMMap64 (`-Et`). This guarantees cold reads from the physical disk and eliminates OS caching effects.

- **Primary**: `RAMMap64.exe -Et` (bundled) — purges the standby list via Sysinternals RAMMap.
- **Fallback**: P/Invoke `NtSetSystemInformation` with `SystemMemoryListInformation` (0x50) — calls the undocumented but stable internal NT API directly.
- If neither works (e.g., not running as Administrator), the test continues with a warning and runs with warm cache.
- Without Administrator privileges, cache clearing is automatically skipped and benchmarks run with warm cache. Use `--no-clear` to suppress the warning.

## Benchmarks

| # | Test | Description |
|---|------|-------------|
| 1 | **Raw sequential read** | Reads the entire file with unbuffered I/O (4 MB buffer, sequential scan hint). Falls back to buffered I/O if unbuffered fails. Reports throughput in MB/s. |
| 2 | **XXH3 sequential hash** | Invokes `xxhsum.exe -H3` on the file to compute an XXH3-128 hash, measuring total elapsed time and effective throughput. |
| 3 | **XXH3 random access** | Seeks to 1024 random 4 KB offsets, reads each block, and pipes all chunks to `xxhsum -H3 -` (stdin mode). Reports total KB read, elapsed time, and throughput. |

Benchmarks 2 and 3 are skipped if `xxhsum.exe` is not found beside the executable.

## Build

```bash
dotnet build -c Release
```
