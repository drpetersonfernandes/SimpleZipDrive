using System.Runtime.CompilerServices;
using SimpleZipDrive.Core;

namespace SimpleZipDrive.Tests.Fakes;

internal static class MockBugReport
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        ErrorLogger.SuppressApiCalls = true;
        ErrorLogger.SuppressApiCalls = true;
    }
}
