# FileBenchmark

A console tool for measuring file I/O performance. Drop a file onto the executable or pass its path as a command-line argument to run a suite of read speed and hashing benchmarks.

## Requirements

- [.NET 10.0 Runtime](https://dotnet.microsoft.com/download)
- `xxhsum.exe` shipped alongside the executable (bundled automatically on build)

## Usage

```
FileBenchmark <filepath>
```

Or drag-and-drop a file onto `FileBenchmark.exe`.

Results are printed to the console and appended to `result.txt` in the executable's directory.

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
