using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

if (args.Length == 0)
{
    Console.WriteLine("Usage: drop a file onto this executable, or run: FileBenchmark <filepath> [--no-clear]");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  --no-clear    Skip cache clearing (warm cache benchmark)");
    Console.WriteLine();
    Console.WriteLine("Note: Cache clearing requires Administrator privileges.");
    Console.WriteLine("      Without admin, benchmarks run with warm cache automatically.");
    Console.WriteLine("Press any key to exit...");
    PauseIfConsole();
    return;
}

var filePath = Path.GetFullPath(args[0]);
var skipCacheClear = args.Any(static a => a.Equals("--no-clear", StringComparison.OrdinalIgnoreCase));

var isRunningAsAdmin = IsAdmin();
Console.WriteLine($"[DEBUG] Running as admin: {isRunningAsAdmin}");

if (!skipCacheClear && !isRunningAsAdmin)
{
    Console.WriteLine("WARNING: Not running as Administrator. Cache clearing will be skipped.");
    Console.WriteLine("         Benchmarks will run with warm cache. Use --no-clear to suppress this warning.");
    skipCacheClear = true;
}

Console.WriteLine($"[DEBUG] Raw arg: {args[0]}");
Console.WriteLine($"[DEBUG] Full path: {filePath}");
Console.WriteLine($"[DEBUG] Path length: {filePath.Length}");
Console.WriteLine($"[DEBUG] Path exists (File.Exists): {File.Exists(filePath)}");
Console.WriteLine($"[DEBUG] Directory exists: {Directory.Exists(Path.GetDirectoryName(filePath))}");
Console.WriteLine($"[DEBUG] Drive info: {Path.GetPathRoot(filePath)}");

try
{
    var di = new DriveInfo(Path.GetPathRoot(filePath)!);
    Console.WriteLine($"[DEBUG] Drive type: {di.DriveType}, format: {di.DriveFormat}, ready: {di.IsReady}");
    if (di.IsReady)
        Console.WriteLine($"[DEBUG] Total size: {di.TotalSize}, available: {di.AvailableFreeSpace}");
}
catch (Exception ex)
{
    Console.WriteLine($"[DEBUG] DriveInfo error: {ex.GetType().Name}: {ex.Message}");
}

try
{
    var dirPath = Path.GetDirectoryName(filePath)!;
    if (Directory.Exists(dirPath))
    {
        var entries = Directory.GetFiles(dirPath);
        Console.WriteLine($"[DEBUG] Files in directory: {entries.Length}");
        foreach (var e in entries.Take(5))
            Console.WriteLine($"[DEBUG]   {e}");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"[DEBUG] Directory listing error: {ex.GetType().Name}: {ex.Message}");
}

try
{
    var fi = new FileInfo(filePath);
    Console.WriteLine($"[DEBUG] FileInfo.Exists: {fi.Exists}");
    Console.WriteLine($"[DEBUG] FileInfo.Attributes: {fi.Attributes}");
}
catch (Exception ex)
{
    Console.WriteLine($"[DEBUG] FileInfo error: {ex.GetType().Name}: {ex.Message}");
}

if (!File.Exists(filePath))
{
    Console.WriteLine($"ERROR: File not found: {filePath}");
    PauseIfConsole();
    return;
}

var fileInfo = new FileInfo(filePath);
var fileSizeBytes = fileInfo.Length;
var fileSizeMb = Math.Round(fileSizeBytes / (1024.0 * 1024.0), 2);
var fileSizeGb = Math.Round(fileSizeBytes / (1024.0 * 1024.0 * 1024.0), 2);

var exeDir = AppContext.BaseDirectory;
var resultFile = Path.Combine(exeDir, "result.txt");
var xxhsumPath = Path.Combine(exeDir, "xxhsum.exe");

var sb = new StringBuilder();
var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

sb.AppendLine("---");
sb.AppendLine(CultureInfo.InvariantCulture, $"Timestamp: {timestamp}");
sb.AppendLine(CultureInfo.InvariantCulture, $"File: {filePath}");
sb.AppendLine(CultureInfo.InvariantCulture, $"Size: {fileSizeMb} MB ({fileSizeGb} GB)");

if (!skipCacheClear)
{
    sb.AppendLine("Cache: cleared before each test (Windows Standby List purged)");
}
else
{
    sb.AppendLine("Cache: warm (not cleared)");
}

// ===== 1. Raw Sequential Read =====
if (!skipCacheClear) ClearWindowsFileCache();
RunSequentialReadTest(filePath, fileSizeBytes, sb);

// ===== 2. XXH3 Sequential Hash =====
if (File.Exists(xxhsumPath))
{
    if (!skipCacheClear) ClearWindowsFileCache();
    RunXxh3SequentialTest(xxhsumPath, filePath, fileSizeBytes, sb);

    // ===== 3. XXH3 Random Access =====
    if (!skipCacheClear) ClearWindowsFileCache();
    RunXxh3RandomAccessTest(xxhsumPath, filePath, fileSizeBytes, sb);
}
else
{
    Console.WriteLine("xxhsum.exe not found, skipping XXH3 benchmarks.");
}

// ===== Write results =====
var output = sb.ToString();
File.AppendAllText(resultFile, output);

Console.WriteLine();
Console.WriteLine(output);
Console.WriteLine($"Results appended to: {resultFile}");
PauseIfConsole();

// ============================================================================
// Cache Clearing
// ============================================================================

const int systemMemoryListInformation = 0x50;
return;

static bool IsAdmin()
{
    using var identity = WindowsIdentity.GetCurrent();
    var principal = new WindowsPrincipal(identity);
    return principal.IsInRole(WindowsBuiltInRole.Administrator);
}

static void PauseIfConsole()
{
    try
    {
        Console.WriteLine("Press any key to exit...");
        Console.ReadKey();
    }
    catch (InvalidOperationException)
    {
    }
}

[DllImport("ntdll.dll", SetLastError = true)]
static extern int NtSetSystemInformation(int systemInformationClass, IntPtr systemInformation, int systemInformationLength);

static void ClearWindowsFileCache()
{
    var ramMapPath = Path.Combine(AppContext.BaseDirectory, "RAMMap64.exe");
    if (!File.Exists(ramMapPath))
    {
        ramMapPath = Path.Combine(AppContext.BaseDirectory, "RAMMap.exe");
    }

    if (File.Exists(ramMapPath))
    {
        Console.Write("Purging Windows Standby List (RAMMap)... ");
        var psi = new ProcessStartInfo
        {
            FileName = ramMapPath,
            Arguments = "-Et",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var proc = Process.Start(psi)!;
        proc.WaitForExit();
        Console.WriteLine("done.");
        return;
    }

    Console.Write("Purging Windows Standby List (ntdll)... ");
    var status = NtSetSystemInformation(systemMemoryListInformation, IntPtr.Zero, 0);
    if (status == 0)
    {
        Console.WriteLine("done.");
        return;
    }

    Console.WriteLine($"failed (NTSTATUS 0x{status:X8}).");
    Console.WriteLine("WARNING: Could not clear system cache. Ensure RAMMap64.exe is present or run as Administrator.");
}

// ============================================================================
// Test 1: Raw Sequential Read
// ============================================================================

static void RunSequentialReadTest(string filePath, long fileSizeBytes, StringBuilder sb)
{
    const int bufferSize = 4 * 1024 * 1024;
    const FileOptions noBuffering = (FileOptions)0x20000000;
    const FileOptions sequentialScan = FileOptions.SequentialScan;

    var sizeGb = Math.Round(fileSizeBytes / (1024.0 * 1024.0 * 1024.0), 2);
    Console.WriteLine($"Reading {filePath} ({sizeGb} GB) with unbuffered I/O...");

    var sw = Stopwatch.StartNew();
    long totalRead = 0;

    try
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, noBuffering | sequentialScan);
        var buffer = new byte[bufferSize];
        int bytesRead;
        while ((bytesRead = fs.Read(buffer, 0, bufferSize)) > 0)
        {
            totalRead += bytesRead;
        }
    }
    catch
    {
        Console.WriteLine("Unbuffered read failed, falling back to buffered...");
        sw.Restart();
        totalRead = 0;
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, FileOptions.SequentialScan);
        var buffer = new byte[bufferSize];
        int bytesRead;
        while ((bytesRead = fs.Read(buffer, 0, bufferSize)) > 0)
        {
            totalRead += bytesRead;
        }
    }

    sw.Stop();

    var readSeconds = Math.Round(sw.Elapsed.TotalSeconds, 3);
    var speedMBs = readSeconds > 0 ? Math.Round(totalRead / (1024.0 * 1024.0) / readSeconds, 2) : 0;

    sb.AppendLine(CultureInfo.InvariantCulture, $"Bytes read: {totalRead}");
    sb.AppendLine(CultureInfo.InvariantCulture, $"Read time: {readSeconds} seconds");
    sb.AppendLine(CultureInfo.InvariantCulture, $"Read speed: {speedMBs} MB/s");

    Console.WriteLine($"Read: {readSeconds}s @ {speedMBs} MB/s");
}

// ============================================================================
// Test 2: XXH3 Sequential Hash
// ============================================================================

static void RunXxh3SequentialTest(string xxhsumPath, string filePath, long fileSizeBytes, StringBuilder sb)
{
    Console.WriteLine("Computing XXH3 sequential hash...");

    var sw = Stopwatch.StartNew();

    var psi = new ProcessStartInfo
    {
        FileName = xxhsumPath,
        Arguments = $"-H3 \"{filePath}\"",
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true
    };

    using var xxhProcess = Process.Start(psi)!;
    var xxhOutput = xxhProcess.StandardOutput.ReadToEnd();
    xxhProcess.WaitForExit();
    sw.Stop();

    var xxh3SeqSeconds = Math.Round(sw.Elapsed.TotalSeconds, 3);
    var xxh3SeqSpeedMBs = xxh3SeqSeconds > 0 ? Math.Round(fileSizeBytes / (1024.0 * 1024.0) / xxh3SeqSeconds, 2) : 0;
    var xxh3SeqHash = xxhOutput.Trim().Split(' ')[0];

    sb.AppendLine("Algorithm: XXH3 (xxhsum sequential)");
    sb.AppendLine(CultureInfo.InvariantCulture, $"XXH3 Hash: {xxh3SeqHash}");
    sb.AppendLine(CultureInfo.InvariantCulture, $"XXH3 time: {xxh3SeqSeconds} seconds");
    sb.AppendLine(CultureInfo.InvariantCulture, $"XXH3 speed: {xxh3SeqSpeedMBs} MB/s");

    Console.WriteLine($"XXH3 Sequential: {xxh3SeqSeconds}s @ {xxh3SeqSpeedMBs} MB/s");
}

// ============================================================================
// Test 3: XXH3 Random Access
// ============================================================================

static void RunXxh3RandomAccessTest(string xxhsumPath, string filePath, long fileSizeBytes, StringBuilder sb)
{
    Console.WriteLine("Computing XXH3 random access read...");

    const int randomBlockCount = 1024;
    const int randomBlockSize = 4096;

    var sw = Stopwatch.StartNew();
    long randomTotalRead = 0;

    var raPsi = new ProcessStartInfo
    {
        FileName = xxhsumPath,
        Arguments = "-H3 -",
        UseShellExecute = false,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true
    };

    using var raProcess = Process.Start(raPsi)!;
    using var raFs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.RandomAccess);

    var maxOffset = fileSizeBytes - randomBlockSize;
    var raBuffer = new byte[randomBlockSize];

    for (var i = 0; i < randomBlockCount; i++)
    {
        var offset = Random.Shared.NextInt64(0, maxOffset + 1);
        raFs.Seek(offset, SeekOrigin.Begin);
        var bytesRead = raFs.Read(raBuffer, 0, randomBlockSize);
        raProcess.StandardInput.BaseStream.Write(raBuffer, 0, bytesRead);
        randomTotalRead += bytesRead;
    }

    raProcess.StandardInput.Close();
    var raHashOutput = raProcess.StandardOutput.ReadToEnd();
    raProcess.WaitForExit();
    sw.Stop();

    var raSeconds = Math.Round(sw.Elapsed.TotalSeconds, 3);
    var raSpeedMBs = raSeconds > 0 ? Math.Round(randomTotalRead / (1024.0 * 1024.0) / raSeconds, 2) : 0;
    var raHash = raHashOutput.Trim().Split(' ')[0];

    sb.AppendLine("Algorithm: XXH3 Random Access");
    sb.AppendLine(CultureInfo.InvariantCulture, $"Random XXH3 Hash: {raHash}");
    sb.AppendLine(CultureInfo.InvariantCulture, $"Random reads: {randomBlockCount} blocks of {randomBlockSize} bytes ({Math.Round(randomTotalRead / 1024.0, 2)} KB total)");
    sb.AppendLine(CultureInfo.InvariantCulture, $"Random access time: {raSeconds} seconds");
    sb.AppendLine(CultureInfo.InvariantCulture, $"Random access speed: {raSpeedMBs} MB/s");

    Console.WriteLine($"XXH3 Random Access: {raSeconds}s @ {raSpeedMBs} MB/s ({randomBlockCount} x {randomBlockSize}B reads)");
}
