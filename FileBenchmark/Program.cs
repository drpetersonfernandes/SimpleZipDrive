using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

if (args.Length == 0)
{
    Console.WriteLine("Usage: drop a file onto this executable, or run: FileBenchmark <filepath>");
    Console.WriteLine("Press any key to exit...");
    Console.ReadKey();
    return;
}

var filePath = Path.GetFullPath(args[0]);

if (!File.Exists(filePath))
{
    Console.WriteLine($"ERROR: File not found: {filePath}");
    Console.WriteLine("Press any key to exit...");
    Console.ReadKey();
    return;
}

var fileInfo = new FileInfo(filePath);
var fileSizeBytes = fileInfo.Length;
var fileSizeMb = Math.Round(fileSizeBytes / (1024.0 * 1024.0), 2);
var fileSizeGb = Math.Round(fileSizeBytes / (1024.0 * 1024.0 * 1024.0), 2);

var exeDir = AppContext.BaseDirectory;
var resultFile = Path.Combine(exeDir, "result.txt");

var sb = new StringBuilder();
var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

sb.AppendLine("---");
sb.AppendLine(CultureInfo.InvariantCulture, $"Timestamp: {timestamp}");
sb.AppendLine(CultureInfo.InvariantCulture, $"File: {filePath}");
sb.AppendLine(CultureInfo.InvariantCulture, $"Size: {fileSizeMb} MB ({fileSizeGb} GB)");

// ===== 1. Read Speed Test =====
Console.WriteLine($"Reading {filePath} ({fileSizeGb} GB) with unbuffered I/O...");

const int bufferSize = 4 * 1024 * 1024;
const FileOptions noBuffering = (FileOptions)0x20000000;
const FileOptions sequentialScan = FileOptions.SequentialScan;

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

// ===== 2. Hash Computation =====
Console.WriteLine("Computing SHA-512 hash...");

sw.Restart();

using var sha512 = SHA512.Create();
using var hashStream = File.OpenRead(filePath);
var hashBytes = sha512.ComputeHash(hashStream);

sw.Stop();

var hashHex = Convert.ToHexString(hashBytes).ToLowerInvariant();
var hashSeconds = Math.Round(sw.Elapsed.TotalSeconds, 3);

sb.AppendLine("Algorithm: SHA-512");
sb.AppendLine(CultureInfo.InvariantCulture, $"Hash: {hashHex}");
sb.AppendLine(CultureInfo.InvariantCulture, $"Hash time: {hashSeconds} seconds");

// ===== Write results =====
var output = sb.ToString();
File.AppendAllText(resultFile, output);

Console.WriteLine();
Console.WriteLine(output);
Console.WriteLine($"Results appended to: {resultFile}");
Console.WriteLine("Press any key to exit...");
Console.ReadKey();
