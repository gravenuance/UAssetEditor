using Microsoft.Extensions.Logging;
using UAssetEditor.Core.Logging;

namespace UAssetEditor.Core.Tests;

/// <summary>
/// A second running instance (or any other process) can hold the day's log file open for
/// writing at the same time - see the real crash this reproduces: <c>FileLoggerProvider</c>'s
/// background writer thread let the resulting <see cref="IOException"/> go unhandled, which
/// terminates the whole process, not just the logging thread.
/// </summary>
public class FileLoggerProviderTests
{
    [Fact]
    public void Log_WhileTheDaysFileIsLockedByAnotherProcess_DoesNotCrashAndRecoversAfterTheLockClears()
    {
        var dir = Path.Combine(Path.GetTempPath(), "UAssetEditorTests_" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"log-{DateTime.UtcNow:yyyyMMdd}.txt");

        var provider = new FileLoggerProvider(dir);
        try
        {
            var logger = provider.CreateLogger("Test");

#pragma warning disable CA1848 // exercising the real ILogger surface here, not a hot path
            using (var exclusiveLock = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
            {
                logger.LogInformation("dropped while locked");
                // Give the background writer thread a chance to hit (and survive) the IOException.
                Thread.Sleep(200);
            }

            logger.LogInformation("written after lock released");
#pragma warning restore CA1848
            Thread.Sleep(200);

            // Dispose before reading back: the provider's own writer still holds the file open.
            provider.Dispose();

            var text = File.ReadAllText(path);
            Assert.Contains("written after lock released", text, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
