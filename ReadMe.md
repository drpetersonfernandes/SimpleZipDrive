# SimpleZipDrive

Mount ZIP, 7Z, and RAR archives as virtual drives on Windows -- no extraction needed. Files are decompressed on-demand as they are read, saving time and disk space.

## Features

- **Read-only virtual drive** -- browse archives like a regular disk
- **Streaming access** -- files are decompressed only when read, not extracted to disk
- **Hybrid caching** -- small files cached in memory, large files cached to temp disk
- **Password support** -- encrypted archives prompt for a password interactively
- **Drag-and-drop** -- drop an archive onto the EXE to auto-mount
- **Graceful shutdown** -- Ctrl+C or closing the window unmounts and cleans up temp files

## Requirements

- Windows 10 or later
- [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Dokan driver](https://github.com/dokan-dev/dokany/releases) installed

## Build

```bash
dotnet build
```

## Run

```
SimpleZipDrive.exe "C:\path\to\archive.zip" M
SimpleZipDrive.exe "C:\path\to\archive.7z" "C:\mount\MyFolder"
```

The mount point can be a drive letter (`M`) or an empty NTFS folder path. Run without arguments to see usage help.

## Test

```bash
dotnet test
```

## License

[GPLv3](LICENSE)
