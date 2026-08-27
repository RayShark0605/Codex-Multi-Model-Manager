using System.Text;
using CodexModelManager.Core.Infrastructure;
using CodexModelManager.Core.Models;

namespace CodexModelManager.Tests;

public sealed class AtomicWriterRemediationTests
{
    [Fact]
    public async Task AbandonedMutexOwnershipIsRecoveredAndAllChecksStillRun()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temporary = new TemporaryDirectory();
        string path = Path.Combine(temporary.Path, "config.toml");
        await File.WriteAllTextAsync(path, "model = \"old\"\n");
        FileFingerprint fingerprint = await FileFingerprintService.CaptureAsync(path);
        string mutexName = "Local\\CodexModelManager.Tests." + Guid.NewGuid().ToString("N");
        using var abandoned = new Mutex(false, mutexName);
        bool acquired = false;
        var owner = new Thread(() =>
        {
            acquired = abandoned.WaitOne(TimeSpan.FromSeconds(5));
            // Deliberately exit without ReleaseMutex to simulate a killed owner.
        });
        owner.Start();
        Assert.True(owner.Join(TimeSpan.FromSeconds(10)));
        Assert.True(acquired);
        int validations = 0;
        var change = new PlannedFileChange(
            path,
            fingerprint,
            Encoding.UTF8.GetBytes("model = \"new\"\n"),
            [],
            _ =>
            {
                validations++;
                return ValueTask.CompletedTask;
            });
        var writer = new AtomicBatchWriter(new FixedDiskSpaceProvider(), mutexName);

        await writer.WriteAsync([change]);

        Assert.Equal(2, validations);
        Assert.Equal("model = \"new\"\n", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task ConcurrentWritersUsingTheSameMutexAreSerialized()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temporary = new TemporaryDirectory();
        string firstPath = Path.Combine(temporary.Path, "first.toml");
        string secondPath = Path.Combine(temporary.Path, "second.toml");
        await File.WriteAllTextAsync(firstPath, "value = 1\n");
        await File.WriteAllTextAsync(secondPath, "value = 2\n");
        int activeValidators = 0;
        int maximumConcurrentValidators = 0;

        async ValueTask Validate(byte[] _)
        {
            int active = Interlocked.Increment(ref activeValidators);
            int observed;
            do
            {
                observed = Volatile.Read(ref maximumConcurrentValidators);
            }
            while (active > observed &&
                   Interlocked.CompareExchange(ref maximumConcurrentValidators, active, observed) != observed);

            try
            {
                await Task.Delay(40);
            }
            finally
            {
                Interlocked.Decrement(ref activeValidators);
            }
        }

        string mutexName = "Local\\CodexModelManager.Tests." + Guid.NewGuid().ToString("N");
        var firstWriter = new AtomicBatchWriter(new FixedDiskSpaceProvider(), mutexName);
        var secondWriter = new AtomicBatchWriter(new FixedDiskSpaceProvider(), mutexName);
        var first = new PlannedFileChange(
            firstPath,
            await FileFingerprintService.CaptureAsync(firstPath),
            Encoding.UTF8.GetBytes("value = 10\n"),
            [],
            Validate);
        var second = new PlannedFileChange(
            secondPath,
            await FileFingerprintService.CaptureAsync(secondPath),
            Encoding.UTF8.GetBytes("value = 20\n"),
            [],
            Validate);

        await Task.WhenAll(firstWriter.WriteAsync([first]), secondWriter.WriteAsync([second]));

        Assert.Equal(1, maximumConcurrentValidators);
        Assert.Equal("value = 10\n", await File.ReadAllTextAsync(firstPath));
        Assert.Equal("value = 20\n", await File.ReadAllTextAsync(secondPath));
    }

    [Fact]
    public async Task RollbackFailurePreservesPrimaryAndRollbackEvidenceInOrder()
    {
        using var temporary = new TemporaryDirectory();
        string path = Path.Combine(temporary.Path, "config.toml");
        await File.WriteAllTextAsync(path, "model = \"original\"\n");
        FileStream? rollbackLock = null;
        string? rollbackPath = null;
        int validations = 0;
        var primary = new InvalidDataException("injected primary validation failure");
        var change = new PlannedFileChange(
            path,
            await FileFingerprintService.CaptureAsync(path),
            Encoding.UTF8.GetBytes("model = \"candidate\"\n"),
            [],
            _ =>
            {
                validations++;
                if (validations == 2)
                {
                    rollbackPath = Assert.Single(Directory.EnumerateFiles(temporary.Path, "*.rollback"));
                    rollbackLock = new FileStream(rollbackPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    throw primary;
                }

                return ValueTask.CompletedTask;
            });
        var writer = new AtomicBatchWriter(new FixedDiskSpaceProvider(), "Local\\CodexModelManager.Tests." + Guid.NewGuid().ToString("N"));

        AggregateException aggregate;
        try
        {
            aggregate = await Assert.ThrowsAsync<AggregateException>(() => writer.WriteAsync([change]));
        }
        finally
        {
            rollbackLock?.Dispose();
        }

        Assert.Equal(2, aggregate.InnerExceptions.Count);
        Assert.Same(primary, aggregate.InnerExceptions[0]);
        Assert.IsType<AggregateException>(aggregate.InnerExceptions[1]);
        Assert.NotNull(rollbackPath);
        Assert.True(File.Exists(rollbackPath));
        Assert.Equal("model = \"candidate\"\n", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public void UncSpaceCheckUsesInjectedProviderWithoutConstructingDriveInfo()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string target = @"\\server\share\codex\config.toml";
        var provider = new RecordingDiskSpaceProvider();
        var change = new PlannedFileChange(
            target,
            FileFingerprint.Missing,
            Encoding.UTF8.GetBytes("model = \"safe\"\n"),
            []);

        AtomicBatchWriter.VerifyAvailableSpace([change], provider);

        Assert.Equal(@"\\server\share", Assert.Single(provider.Roots));
    }

    private sealed class FixedDiskSpaceProvider : IAvailableDiskSpaceProvider
    {
        public AvailableDiskSpace GetAvailableSpace(string rootPath) => new(true, long.MaxValue);
    }

    private sealed class RecordingDiskSpaceProvider : IAvailableDiskSpaceProvider
    {
        public List<string> Roots { get; } = [];

        public AvailableDiskSpace GetAvailableSpace(string rootPath)
        {
            Roots.Add(rootPath);
            return new AvailableDiskSpace(true, long.MaxValue);
        }
    }
}
