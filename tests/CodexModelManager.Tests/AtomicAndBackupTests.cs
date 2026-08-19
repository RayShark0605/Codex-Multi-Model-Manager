using System.Text;
using CodexModelManager.Core.Abstractions;
using CodexModelManager.Core.Backup;
using CodexModelManager.Core.Codex;
using CodexModelManager.Core.Infrastructure;
using CodexModelManager.Core.Models;

namespace CodexModelManager.Tests;

public sealed class AtomicAndBackupTests
{
    [Fact]
    public async Task ExternalModificationStopsWrite()
    {
        using var root = new TemporaryDirectory();
        string path = Path.Combine(root.Path, "config.toml");
        await File.WriteAllTextAsync(path, "model = \"a\"\n");
        FileFingerprint fingerprint = await FileFingerprintService.CaptureAsync(path);
        await File.WriteAllTextAsync(path, "model = \"external\"\n");
        var change = new PlannedFileChange(path, fingerprint, Encoding.UTF8.GetBytes("model = \"b\"\n"), []);

        IOException error = await Assert.ThrowsAsync<IOException>(() => new AtomicBatchWriter().WriteAsync([change]));
        Assert.Contains("发生变化", error.Message, StringComparison.Ordinal);
        Assert.Equal("model = \"external\"\n", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task PostCommitValidationFailureRollsBackOriginal()
    {
        using var root = new TemporaryDirectory();
        string path = Path.Combine(root.Path, "config.toml");
        byte[] original = Encoding.UTF8.GetBytes("model = \"original\"\n");
        await File.WriteAllBytesAsync(path, original);
        FileFingerprint fingerprint = await FileFingerprintService.CaptureAsync(path);
        int validations = 0;
        var change = new PlannedFileChange(path, fingerprint, Encoding.UTF8.GetBytes("model = \"candidate\"\n"), [], _ =>
        {
            validations++;
            if (validations == 2) throw new InvalidDataException("injected post-commit validation failure");
            return ValueTask.CompletedTask;
        });

        await Assert.ThrowsAsync<InvalidDataException>(() => new AtomicBatchWriter().WriteAsync([change]));
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task InitialSnapshotIsImmutableAndRecordsMissingModelsJson()
    {
        using var root = new TemporaryDirectory();
        var home = new TestCodexHomeProvider(Path.Combine(root.Path, "home"));
        await File.WriteAllTextAsync(Path.Combine(home.Home, "config.toml"), "model = \"first\"\n");
        var patch = new TomlConfigPatchEngine();
        var backups = new BackupService(home, new AtomicBatchWriter(), patch, "test", "codex-cli-test");
        string initial = await backups.EnsureInitialSnapshotAsync();
        string firstBytes = await File.ReadAllTextAsync(Path.Combine(initial, "config.toml"));
        await File.WriteAllTextAsync(Path.Combine(home.Home, "config.toml"), "model = \"second\"\n");
        Assert.Equal(initial, await backups.EnsureInitialSnapshotAsync());
        Assert.Equal(firstBytes, await File.ReadAllTextAsync(Path.Combine(initial, "config.toml")));
        string manifest = await File.ReadAllTextAsync(Path.Combine(initial, "manifest.json"));
        Assert.Contains("\"relativeName\": \"models.json\"", manifest, StringComparison.Ordinal);
        Assert.Contains("\"existed\": false", manifest, StringComparison.Ordinal);
        Assert.Contains("\"codexVersion\": \"codex-cli-test\"", manifest, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CorruptInitialSnapshotBlocksReuseWithoutOverwritingIt()
    {
        using var root = new TemporaryDirectory();
        var home = new TestCodexHomeProvider(Path.Combine(root.Path, "home"));
        string config = Path.Combine(home.Home, "config.toml");
        await File.WriteAllTextAsync(config, "model = \"safe\"\n");
        var backups = new BackupService(home, new AtomicBatchWriter(), new TomlConfigPatchEngine());
        string initial = await backups.EnsureInitialSnapshotAsync();
        await File.WriteAllTextAsync(Path.Combine(initial, "config.toml"), "model = \"tampered\"\n");

        await Assert.ThrowsAsync<InvalidDataException>(() => backups.EnsureInitialSnapshotAsync());

        Assert.Equal("model = \"safe\"\n", await File.ReadAllTextAsync(config));
        Assert.Equal("model = \"tampered\"\n", await File.ReadAllTextAsync(Path.Combine(initial, "config.toml")));
    }

    [Fact]
    public async Task RestoreCreatesHistoryAndCanRestoreMissingState()
    {
        using var root = new TemporaryDirectory();
        var home = new TestCodexHomeProvider(Path.Combine(root.Path, "home"));
        string config = Path.Combine(home.Home, "config.toml");
        await File.WriteAllTextAsync(config, "model = \"initial\"\n");
        var patch = new TomlConfigPatchEngine();
        var backups = new BackupService(home, new AtomicBatchWriter(), patch, "test");
        string initial = await backups.EnsureInitialSnapshotAsync();
        await File.WriteAllTextAsync(config, "model = \"changed\"\n");
        await File.WriteAllTextAsync(Path.Combine(home.Home, "models.json"), "{\"models\":[]}");
        await backups.RestoreAsync(initial);
        Assert.Equal("model = \"initial\"\n", await File.ReadAllTextAsync(config));
        Assert.False(File.Exists(Path.Combine(home.Home, "models.json")));
        Assert.NotEmpty(await backups.ListHistoryAsync());
    }

    [Fact]
    public async Task DeepSeekOfficialBackupCoexistsUntouched()
    {
        using var root = new TemporaryDirectory();
        var home = new TestCodexHomeProvider(Path.Combine(root.Path, "home"));
        await File.WriteAllTextAsync(Path.Combine(home.Home, "config.toml"), "model = \"x\"\n");
        string official = Path.Combine(home.Home, "backup-deepseek");
        Directory.CreateDirectory(official);
        string marker = Path.Combine(official, "marker.txt");
        await File.WriteAllTextAsync(marker, "do-not-touch");
        var backups = new BackupService(home, new AtomicBatchWriter(), new TomlConfigPatchEngine());
        await backups.EnsureInitialSnapshotAsync();
        await backups.CreateHistorySnapshotAsync(BackupOperation.Manual, null, null, null, null);
        Assert.Equal("do-not-touch", await File.ReadAllTextAsync(marker));
        Assert.True(Directory.Exists(official));
    }

    [Fact]
    public async Task LockedConfigCannotBeOverwritten()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var root = new TemporaryDirectory();
        string path = Path.Combine(root.Path, "config.toml");
        await File.WriteAllTextAsync(path, "model = \"original\"\n");
        FileFingerprint fingerprint = await FileFingerprintService.CaptureAsync(path);
        await using (FileStream locked = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var change = new PlannedFileChange(path, fingerprint, Encoding.UTF8.GetBytes("model = \"new\"\n"), []);
            await Assert.ThrowsAnyAsync<IOException>(() => new AtomicBatchWriter().WriteAsync([change]));
        }

        Assert.Equal("model = \"original\"\n", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task OldOrCorruptManifestIsListedInvalidWithoutTouchingConfig()
    {
        using var root = new TemporaryDirectory();
        var home = new TestCodexHomeProvider(Path.Combine(root.Path, "home"));
        string config = Path.Combine(home.Home, "config.toml");
        await File.WriteAllTextAsync(config, "model = \"safe\"\n");
        var backups = new BackupService(home, new AtomicBatchWriter(), new TomlConfigPatchEngine());
        string broken = Path.Combine(backups.BackupRoot, "history", "20000101-000000000");
        Directory.CreateDirectory(broken);
        await File.WriteAllTextAsync(Path.Combine(broken, "manifest.json"), "{broken");
        BackupSnapshotInfo item = Assert.Single(await backups.ListHistoryAsync());
        Assert.False(item.HashesValid);
        Assert.Equal("model = \"safe\"\n", await File.ReadAllTextAsync(config));
    }

    [Fact]
    public async Task MultiFilePostCommitFailureRollsBackEveryTarget()
    {
        using var root = new TemporaryDirectory();
        string external = Path.Combine(root.Path, "agent.config.toml");
        string config = Path.Combine(root.Path, "config.toml");
        await File.WriteAllTextAsync(external, "model = \"external-old\"\n");
        await File.WriteAllTextAsync(config, "model = \"config-old\"\n");
        int configValidations = 0;
        PlannedFileChange[] changes =
        [
            new(external, await FileFingerprintService.CaptureAsync(external), Encoding.UTF8.GetBytes("model = \"external-new\"\n"), []),
            new(config, await FileFingerprintService.CaptureAsync(config), Encoding.UTF8.GetBytes("model = \"config-new\"\n"), [], _ =>
            {
                if (++configValidations == 2) throw new InvalidDataException("injected final validation failure");
                return ValueTask.CompletedTask;
            }),
        ];

        await Assert.ThrowsAsync<InvalidDataException>(() => new AtomicBatchWriter().WriteAsync(changes));

        Assert.Equal("model = \"external-old\"\n", await File.ReadAllTextAsync(external));
        Assert.Equal("model = \"config-old\"\n", await File.ReadAllTextAsync(config));
    }

    [Fact]
    public async Task ExplicitCommitLastOrdersPrimaryConfigAfterDependencies()
    {
        using var root = new TemporaryDirectory();
        string config = Path.Combine(root.Path, "config.toml");
        string external = Path.Combine(root.Path, "project", ".codex", "config.toml");
        Directory.CreateDirectory(Path.GetDirectoryName(external)!);
        await File.WriteAllTextAsync(config, "model = \"main-old\"\n");
        await File.WriteAllTextAsync(external, "model = \"agent-old\"\n");
        List<string> validationOrder = [];
        PlannedFileChange[] changes =
        [
            new(config, await FileFingerprintService.CaptureAsync(config), Encoding.UTF8.GetBytes("model = \"main-new\"\n"), [], _ => { validationOrder.Add("main"); return ValueTask.CompletedTask; }, CommitLast: true),
            new(external, await FileFingerprintService.CaptureAsync(external), Encoding.UTF8.GetBytes("model = \"agent-new\"\n"), [], _ => { validationOrder.Add("external"); return ValueTask.CompletedTask; }),
        ];

        await new AtomicBatchWriter().WriteAsync(changes);

        Assert.Equal(["external", "main", "external", "main"], validationOrder);
    }

    [Fact]
    public async Task SupplementalBaselineIsImmutableAndCorruptionBlocksReuse()
    {
        using var root = new TemporaryDirectory();
        var home = new TestCodexHomeProvider(Path.Combine(root.Path, "home"));
        await File.WriteAllTextAsync(Path.Combine(home.Home, "config.toml"), "model = \"main\"\n");
        string external = Path.Combine(root.Path, "agent.config.toml");
        await File.WriteAllTextAsync(external, "model = \"original\"\n");
        var backups = new BackupService(home, new AtomicBatchWriter(), new TomlConfigPatchEngine());
        await backups.EnsureSupplementalBaselinesAsync([external]);
        string baseline = Assert.Single(Directory.EnumerateDirectories(Path.Combine(backups.BackupRoot, "supplemental-baseline")));
        await File.WriteAllTextAsync(external, "model = \"changed\"\n");

        await backups.EnsureSupplementalBaselinesAsync([external]);
        Assert.Equal("model = \"original\"\n", await File.ReadAllTextAsync(Path.Combine(baseline, "content.toml")));
        await File.WriteAllTextAsync(Path.Combine(baseline, "content.toml"), "corrupt");

        await Assert.ThrowsAsync<InvalidDataException>(() => backups.EnsureSupplementalBaselinesAsync([external]));
    }
}
