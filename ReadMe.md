# Simple Zip Drive for Windows

This application allows you to mount ZIP archive files as virtual drives or directories on your Windows system using the DokanNet library. It provides read-only access to the contents of the ZIP file as if it were a regular part of your filesystem.

It accesses the source ZIP file via a file stream directly from disk. This approach supports very large archives without consuming excessive RAM for the archive itself, regardless of its size.

For files *within* the ZIP archive, when an application (like an emulator) opens a file for reading, its entire decompressed content is cached into an in-memory stream. This significantly speeds up random access reads required by many applications.

## Features

*   Mount ZIP archives as virtual drives (e.g., `M:\`) or to an NTFS folder mount point (e.g., `C:\mount\myzip`).
*   **Drag-and-drop a ZIP file onto the `SimpleZipDrive.exe` icon to automatically attempt mounting it.** It will try drive letters M:, N:, O:, P:, then Q: in sequence.
*   Read-only access to the ZIP contents.
*   Efficiently handles ZIP files of all sizes by streaming the main archive from disk.
*   Caches individual decompressed file entries from the ZIP into memory upon first access for fast subsequent reads.
*   Handles basic file and directory information (names, sizes, timestamps).
*   Basic wildcard support for file searching within the mounted ZIP.

## Prerequisites

1.  **.NET Runtime:** The application is built for .NET 9.0 (or a compatible newer version). You'll need the .NET Desktop Runtime installed.
2.  **Dokan Library:** This application depends on the Dokan user-mode file system library for Windows.
    *   Download and install the latest Dokan library from the official DokanNet GitHub releases: [https://github.com/dokan-dev/dokany/releases](https://github.com/dokan-dev/dokany/releases).

## How to Build

1.  Clone or download this repository/source code.
2.  Open the solution in Visual Studio (2022 or later recommended) or use the .NET CLI.
3.  Ensure the `DokanNet` and `SharpZipLib` NuGet packages are restored. The provided `.csproj` file includes these dependencies.
4.  Build the solution (e.g., `dotnet build -c Release`).

The executable `SimpleZipDrive.exe` will be in the `bin\Release\net9.0-windows` (or similar) directory.

## How to Use

There are two main ways to use Simple Zip Drive:

**1. Command-Line (Explicit Mount Point):**

Run the application from the command line specifying the ZIP file and the desired mount point:

```shell
SimpleZipDrive.exe <PathToZipFile> <MountPoint>
```

**Arguments:**

*   `<PathToZipFile>`: The full path to the ZIP archive you want to mount.
    *   Example: `C:\Users\YourName\Downloads\my_archive.zip`
*   `<MountPoint>`: The desired mount point. This can be:
    *   A single drive letter (e.g., `M`, `X`, `Z`). The application will append `:\`.
    *   A full path to an NTFS directory (e.g., `C:\mount\my_virtual_zip`). If the directory does not exist, it will be created automatically. The directory should ideally be empty.

**Command-Line Examples:**

*   Mount `archive.zip` to drive `M:`
    ```shell
    SimpleZipDrive.exe "C:\path\to\archive.zip" M
    ```
    The ZIP contents will be accessible at `M:\`.

*   Mount `another_archive.zip` to a folder `C:\myvirtualdrive`:
    ```shell
    SimpleZipDrive.exe "D:\games\game_files.zip" "C:\myvirtualdrive"
    ```
    The ZIP contents will be accessible at `C:\myvirtualdrive\`.

**2. Drag-and-Drop (Automatic Mount Point):**

*   Simply drag your `.zip` file from Windows Explorer and drop it onto the `SimpleZipDrive.exe` icon.
*   The application will attempt to mount the ZIP file automatically. It will first try to use drive letter `M:\`. If `M:\` is unavailable, it will try `N:\`, then `O:\`, `P:\`, and finally `Q:\`.
*   If a mount is successful, the console window will remain open, showing the active mount.
*   If all preferred drive letters (M-Q) are unavailable or an error occurs, the console window will remain open displaying the error messages.

**To Unmount (for both methods):**

*   Press `Ctrl+C` in the console window where `SimpleZipDrive.exe` is running.
*   Alternatively, simply close the console window.

The application will attempt to unmount the virtual drive/directory upon exit.

## Important Notes

*   **Administrator Privileges:** Mounting to a drive letter or certain system paths might require running the application as an Administrator. If you encounter "Access Denied" or "MountPoint" errors (especially with drag-and-drop if preferred drive letters are in use by system processes or require elevation), try running `SimpleZipDrive.exe` from an administrative command prompt, or by right-clicking the .exe and choosing "Run as administrator" before dragging a file onto it.
*   **Read-Only:** This is a read-only filesystem. You cannot write, delete, or modify files within the mounted ZIP.
*   **Memory Usage:**
    *   The source ZIP file itself is always streamed from disk, minimizing initial RAM usage for the archive.
    *   Individual files *inside* the ZIP are fully decompressed and cached into memory when an application (like an emulator) opens them for reading. This is done to provide fast random access. If many large files are opened simultaneously by the accessing application, the `SimpleZipDrive.exe` process could consume significant RAM. Memory for a cached file is released when the accessing application closes its handle to that file.
*   **Dokan Driver:** Ensure the Dokan driver is correctly installed and running. If you have issues, reinstalling Dokan might help.
*   **Error Handling:** The application includes basic error handling and logging to the console. If a mount fails (either via command-line or drag-and-drop), the console window will remain open with error details. For more detailed Dokan-level debugging, you can uncomment the `DokanOptions.DebugMode` and `DokanOptions.StderrOutput` lines in `Program.cs` and use a tool like DebugView (DbgView.exe from Sysinternals) to capture kernel messages.

## Troubleshooting

*   **"DOKAN INITIALIZATION FAILED" on startup**:
    *   This error means the application could not find the Dokan library (`dokan.dll`) or communicate with the Dokan driver.
    *   The most common cause is that **Dokan is not installed**. Please download and install it from the official source: [https://github.com/dokan-dev/dokany/releases](https://github.com/dokan-dev/dokany/releases).
    *   After installation, please try running the application again.
*   **"Dokan Error: ... MountPoint ... AssignDriveLetter ..." during mount**:
    *   The mount point (either specified or one of M-Q in drag-and-drop) might already be in use.
    *   You might need administrator privileges (see "Important Notes").
    *   If mounting to a folder, the application will attempt to create it if it doesn't exist. This can fail due to insufficient permissions (try running as Administrator) or an invalid path.
    *   Ensure the Dokan driver is installed and functioning. You can get it from [https://github.com/dokan-dev/dokany/releases](https://github.com/dokan-dev/dokany/releases).
*   **Drag-and-Drop Fails to Mount**:
    *   All preferred drive letters (M:, N:, O:, P:, Q:) might be in use or require administrator privileges to access. Check the console output for specific errors.
    *   Try running `SimpleZipDrive.exe` as an administrator first, then drag the file onto it.
*   **"Error: ZIP file not found..."**: Double-check the path to your ZIP file (for command-line usage).
*   **"Out of Memory Error"**:
    *   This typically happens during `CreateFile` (logged by `ZipFs`), meaning an individual file *within* the ZIP was too large to cache in memory (either >1GB or system RAM exhausted by cumulative caching of multiple files). This can occur if the accessing application opens very large files or many files simultaneously.
*   **Application (e.g., an emulator) fails to read files correctly:**
    *   Check the console output of `SimpleZipDrive.exe` for any errors logged by `ZipFs`.
    *   Enable Dokan kernel logging (see "Important Notes") and use DbgView to look for lower-level errors.

## Support the Project

If you find Simple Zip Drive useful, please consider supporting its development:

*   **Star the Repository:** Show your appreciation by starring the project on GitHub!
*   **Donate:** Contributions help cover development time and costs. You can donate at: [https://purelogiccode.com/Donate](https://purelogiccode.com/Donate)

The developer's website is [PureLogic Code](https://purelogiccode.com/).

## License

This project has a GPL-3.0 license. The DokanNet and SharpZipLib libraries have an MIT license. The underlying Dokan library contains LGPL and MIT licensed programs.

## Acknowledgements

*   [DokanNet](https://github.com/dokan-dev/dokan-dotnet) - .NET wrapper for Dokan
*   [Dokan](https://github.com/dokan-dev/dokany) - User-mode file system library for Windows
*   [SharpZipLib](https://github.com/icsharpcode/SharpZipLib) - A comprehensive Zip, GZip, Tar and BZip2 library for .NET