using System.Diagnostics;
using System.Globalization;
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

// ===== 2. XXH3 Sequential Hash (Raw Read Speed) =====
var xxhsumPath = Path.Combine(exeDir, "xxhsum.exe");

if (File.Exists(xxhsumPath))
{
    Console.WriteLine("Computing XXH3 sequential hash...");

    sw.Restart();

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

    // ===== 3. XXH3 Random Access Read =====
    Console.WriteLine("Computing XXH3 random access read...");

    const int randomBlockCount = 1024;
    const int randomBlockSize = 4096;

    sw.Restart();
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
Console.WriteLine("Press any key to exit...");
Console.ReadKey();
