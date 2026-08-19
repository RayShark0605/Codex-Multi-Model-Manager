using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodexModelManager.Core.Abstractions;
using CodexModelManager.Core.Infrastructure;
using CodexModelManager.Core.Models;

namespace CodexModelManager.Core.Backup;

public sealed class BackupService : IBackupService
{
    private static readonly string[] PrimaryFileNames = ["config.toml", "models.json"];
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ICodexHomeProvider homeProvider;
    private readonly IAtomicBatchWriter writer;
    private readonly IConfigPatchEngine configValidator;
    private readonly string appVersion;

    public BackupService(
        ICodexHomeProvider homeProvider,
        IAtomicBatchWriter writer,
        IConfigPatchEngine configValidator,
        string? appVersion = null,
        string? codexVersion = null)
    {
        this.homeProvider = homeProvider;
        this.writer = writer;
        this.configValidator = configValidator;
        this.appVersion = appVersion ?? typeof(BackupService).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        CodexVersion = codexVersion;
        BackupRoot = Path.Combine(homeProvider.GetCodexHome(), "model-switcher-backup");
    }

    public string BackupRoot { get; }

    public string? CodexVersion { get; set; }

    public async Task<string> EnsureInitialSnapshotAsync(CancellationToken cancellationToken = default)
    {
        string directory = Path.Combine(BackupRoot, "initial");
        if (File.Exists(Path.Combine(directory, "manifest.json")))
        {
            await ValidateInitialSnapshotAsync(directory, cancellationToken).ConfigureAwait(false);
            return directory;
        }

        Directory.CreateDirectory(BackupRoot);
        string staging = Path.Combine(BackupRoot, ".initial-" + Guid.NewGuid().ToString("N"));
        try
        {
            await CreateSnapshotDirectoryAsync(
                staging,
                BackupOperation.InitialSnapshot,
                null,
                null,
                null,
                null,
                [],
                [],
                cancellationToken).ConfigureAwait(false);
            try
            {
                Directory.Move(staging, directory);
            }
            catch (IOException) when (Directory.Exists(directory))
            {
                // Another instance won the first-run race. Initial is immutable.
                if (!File.Exists(Path.Combine(directory, "manifest.json")))
                {
                    throw new InvalidDataException("Initial Snapshot 目录已存在但不完整；为避免覆盖，已停止。请人工检查该目录。");
                }
            }

            await ValidateInitialSnapshotAsync(directory, cancellationToken).ConfigureAwait(false);
            return directory;
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }

    public async Task<string> CreateHistorySnapshotAsync(
        BackupOperation operation,
        string? sourceProvider,
        string? sourceModel,
        string? targetProvider,
        string? targetModel,
        IReadOnlyCollection<string>? changedKeys = null,
        IReadOnlyCollection<string>? additionalFiles = null,
        CancellationToken cancellationToken = default)
    {
        string historyRoot = Path.Combine(BackupRoot, "history");
        Directory.CreateDirectory(historyRoot);
        string staging = Path.Combine(historyRoot, ".snapshot-" + Guid.NewGuid().ToString("N"));
        try
        {
            await CreateSnapshotDirectoryAsync(
                staging,
                operation,
                sourceProvider,
                sourceModel,
                targetProvider,
                targetModel,
                changedKeys ?? [],
                additionalFiles ?? [],
                cancellationToken).ConfigureAwait(false);
            while (true)
            {
                string directory = Path.Combine(historyRoot, DateTime.Now.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture));
                try
                {
                    Directory.Move(staging, directory);
                    return directory;
                }
                catch (IOException) when (Directory.Exists(directory))
                {
                    await Task.Delay(2, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }

    public async Task EnsureSupplementalBaselinesAsync(
        IReadOnlyCollection<string> files,
        CancellationToken cancellationToken = default)
    {
        foreach (string file in files.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ValidateSupplementalTarget(file);
            string identifier = PathIdentifier(file);
            string root = Path.Combine(BackupRoot, "supplemental-baseline");
            string directory = Path.Combine(root, identifier);
            if (File.Exists(Path.Combine(directory, "manifest.json")))
            {
                await ValidateSupplementalBaselineAsync(directory, file, cancellationToken).ConfigureAwait(false);
                continue;
            }

            Directory.CreateDirectory(root);
            string staging = Path.Combine(root, $".{identifier}-{Guid.NewGuid():N}");
            try
            {
                await CreateSupplementalBaselineAsync(staging, file, cancellationToken).ConfigureAwait(false);
                try
                {
                    Directory.Move(staging, directory);
                }
                catch (IOException) when (Directory.Exists(directory))
                {
                    if (!File.Exists(Path.Combine(directory, "manifest.json")))
                    {
                        throw new InvalidDataException("Supplemental baseline 目录已存在但不完整；为避免覆盖，已停止。");
                    }

                    await ValidateSupplementalBaselineAsync(directory, file, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                if (Directory.Exists(staging)) Directory.Delete(staging, true);
            }
        }
    }

    public async Task<IReadOnlyList<BackupSnapshotInfo>> ListHistoryAsync(CancellationToken cancellationToken = default)
    {
        List<BackupSnapshotInfo> result = [];
        string historyRoot = Path.Combine(BackupRoot, "history");
        IEnumerable<string> directories = Directory.Exists(historyRoot)
            ? Directory.EnumerateDirectories(historyRoot)
                .Where(path => !Path.GetFileName(path).StartsWith('.'))
                .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
            : [];
        foreach (string directory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string manifestPath = Path.Combine(directory, "manifest.json");
            try
            {
                BackupManifest? manifest = JsonSerializer.Deserialize<BackupManifest>(
                    await File.ReadAllBytesAsync(manifestPath, cancellationToken).ConfigureAwait(false),
                    JsonOptions);
                if (manifest is not null)
                {
                    result.Add(new BackupSnapshotInfo(directory, manifest, await ValidateHashesAsync(directory, manifest, cancellationToken).ConfigureAwait(false)));
                }
            }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
            {
                result.Add(new BackupSnapshotInfo(directory, new BackupManifest { CreatedAt = "损坏或不兼容" }, false));
            }
        }

        return result;
    }

    public async Task RestoreAsync(string snapshotDirectory, CancellationToken cancellationToken = default)
    {
        string snapshot = Path.GetFullPath(snapshotDirectory);
        string allowedRoot = Path.GetFullPath(BackupRoot) + Path.DirectorySeparatorChar;
        if (!snapshot.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase) &&
            !snapshot.Equals(Path.Combine(Path.GetFullPath(BackupRoot), "initial"), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("拒绝恢复备份根目录以外的快照。");
        }

        string manifestPath = Path.Combine(snapshot, "manifest.json");
        BackupManifest manifest = JsonSerializer.Deserialize<BackupManifest>(
            await File.ReadAllBytesAsync(manifestPath, cancellationToken).ConfigureAwait(false),
            JsonOptions) ?? throw new InvalidDataException("备份 manifest 为空。");
        if (!await ValidateHashesAsync(snapshot, manifest, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("备份 SHA-256 校验失败，已拒绝恢复。");
        }

        List<RestoreSource> restoreSources = manifest.Files
            .Select(file => new RestoreSource(file, snapshot, file.RelativeName, file.RelativeName is not ("config.toml" or "models.json")))
            .ToList();
        if (snapshot.Equals(Path.Combine(Path.GetFullPath(BackupRoot), "initial"), StringComparison.OrdinalIgnoreCase))
        {
            string supplementalRoot = Path.Combine(BackupRoot, "supplemental-baseline");
            foreach (string directory in (Directory.Exists(supplementalRoot) ? Directory.EnumerateDirectories(supplementalRoot) : [])
                         .Where(path => !Path.GetFileName(path).StartsWith('.')))
            {
                BackupManifest supplemental = JsonSerializer.Deserialize<BackupManifest>(
                    await File.ReadAllBytesAsync(Path.Combine(directory, "manifest.json"), cancellationToken).ConfigureAwait(false),
                    JsonOptions) ?? throw new InvalidDataException("Supplemental baseline manifest 为空。");
                BackupFileManifest supplementalFile = supplemental.Files.Count == 1
                    ? supplemental.Files[0]
                    : throw new InvalidDataException("Supplemental baseline 文件清单无效。");
                await ValidateSupplementalBaselineAsync(directory, supplementalFile.OriginalPath, cancellationToken).ConfigureAwait(false);
                restoreSources.Add(new RestoreSource(supplementalFile, directory, supplementalFile.RelativeName, true));
            }
        }

        string home = homeProvider.GetCodexHome();
        Dictionary<RestoreSource, string> targets = restoreSources.ToDictionary(source => source, source => ResolveRestoreTarget(home, source.File, source.IsSupplemental));
        string[] supplementalTargets = targets
            .Where(pair => pair.Key.IsSupplemental)
            .Select(pair => pair.Value)
            .ToArray();
        Dictionary<string, FileFingerprint> beforeBackup = new(StringComparer.OrdinalIgnoreCase);
        foreach (string target in targets.Values.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            beforeBackup[target] = await FileFingerprintService.CaptureAsync(target, cancellationToken).ConfigureAwait(false);
        }

        await CreateHistorySnapshotAsync(
            snapshot.EndsWith("initial", StringComparison.OrdinalIgnoreCase) ? BackupOperation.RestoreInitial : BackupOperation.RestorePrevious,
            null,
            null,
            manifest.SourceProvider,
            manifest.SourceModel,
            ["restore"],
            supplementalTargets,
            cancellationToken).ConfigureAwait(false);

        foreach ((string target, FileFingerprint expected) in beforeBackup)
        {
            FileFingerprint actual = await FileFingerprintService.CaptureAsync(target, cancellationToken).ConfigureAwait(false);
            if (!FileFingerprintService.Matches(expected, actual))
            {
                throw new IOException($"配置文件在恢复备份期间发生变化，请重新加载: {Path.GetFileName(target)}");
            }
        }

        List<PlannedFileChange> changes = [];
        foreach (RestoreSource source in restoreSources)
        {
            BackupFileManifest file = source.File;
            string target = targets[source];
            FileFingerprint expected = beforeBackup[target];
            byte[]? candidate = file.Existed
                ? await File.ReadAllBytesAsync(ResolveSnapshotContentPath(source.SnapshotDirectory, source.StorageRelativeName), cancellationToken).ConfigureAwait(false)
                : null;
            Func<byte[], ValueTask>? validator = target.EndsWith(".toml", StringComparison.OrdinalIgnoreCase)
                ? bytes =>
                {
                    TextFileSnapshot decoded = DecodeSnapshot(target, bytes);
                    configValidator.Validate(decoded.Text);
                    return ValueTask.CompletedTask;
                }
            : file.RelativeName == "models.json" ? bytes =>
            {
                using JsonDocument _ = JsonDocument.Parse(bytes);
                return ValueTask.CompletedTask;
            }
            : null;
            bool commitLast = target.Equals(Path.Combine(home, "config.toml"), StringComparison.OrdinalIgnoreCase);
            changes.Add(new PlannedFileChange(target, expected, candidate, [], candidate is null ? null : validator, commitLast));
        }

        await writer.WriteAsync(changes, cancellationToken).ConfigureAwait(false);
    }

    private async Task CreateSnapshotDirectoryAsync(
        string directory,
        BackupOperation operation,
        string? sourceProvider,
        string? sourceModel,
        string? targetProvider,
        string? targetModel,
        IReadOnlyCollection<string> changedKeys,
        IReadOnlyCollection<string> additionalFiles,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        var manifest = new BackupManifest
        {
            Operation = operation,
            CreatedAt = DateTimeOffset.Now.ToString("O"),
            AppVersion = appVersion,
            CodexVersion = CodexVersion,
            SourceProvider = sourceProvider,
            SourceModel = sourceModel,
            TargetProvider = targetProvider,
            TargetModel = targetModel,
            ChangedKeys = changedKeys.Order(StringComparer.Ordinal).ToList(),
        };

        foreach (string name in PrimaryFileNames)
        {
            string source = Path.Combine(homeProvider.GetCodexHome(), name);
            await AddSnapshotFileAsync(directory, manifest, source, name, cancellationToken).ConfigureAwait(false);
        }

        HashSet<string> primaryFiles = new(
            PrimaryFileNames.Select(name => Path.GetFullPath(Path.Combine(homeProvider.GetCodexHome(), name))),
            StringComparer.OrdinalIgnoreCase);
        foreach (string source in additionalFiles.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (primaryFiles.Contains(source)) continue;
            ValidateSupplementalTarget(source);
            string relative = Path.Combine("supplemental", PathIdentifier(source) + ".toml");
            await AddSnapshotFileAsync(directory, manifest, source, relative, cancellationToken).ConfigureAwait(false);
        }

        await WriteManifestAsync(directory, manifest, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> ValidateHashesAsync(string directory, BackupManifest manifest, CancellationToken cancellationToken)
    {
        foreach (BackupFileManifest file in manifest.Files)
        {
            string path;
            try { path = ResolveSnapshotContentPath(directory, file.RelativeName); }
            catch (InvalidDataException) { return false; }
            if (!file.Existed)
            {
                if (File.Exists(path)) return false;
                continue;
            }

            FileFingerprint fingerprint = await FileFingerprintService.CaptureAsync(path, cancellationToken).ConfigureAwait(false);
            if (!fingerprint.Exists || fingerprint.Length != file.Length || !fingerprint.Sha256.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private async Task ValidateInitialSnapshotAsync(string directory, CancellationToken cancellationToken)
    {
        BackupManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<BackupManifest>(
                await File.ReadAllBytesAsync(Path.Combine(directory, "manifest.json"), cancellationToken).ConfigureAwait(false),
                JsonOptions) ?? throw new InvalidDataException("Initial Snapshot manifest 为空。");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Initial Snapshot manifest 无效；已拒绝覆盖或继续使用。", exception);
        }

        string home = Path.GetFullPath(homeProvider.GetCodexHome());
        bool validShape = manifest.Operation == BackupOperation.InitialSnapshot &&
            manifest.Files.Count == PrimaryFileNames.Length &&
            PrimaryFileNames.All(name => manifest.Files.Any(file =>
                file.RelativeName.Equals(name, StringComparison.Ordinal) &&
                Path.GetFullPath(file.OriginalPath).Equals(Path.Combine(home, name), StringComparison.OrdinalIgnoreCase)));
        if (!validShape || !await ValidateHashesAsync(directory, manifest, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("Initial Snapshot 结构或 SHA-256 校验失败；为避免覆盖，已停止。请人工检查该目录。");
        }
    }

    private async Task CreateSupplementalBaselineAsync(string directory, string source, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        var manifest = new BackupManifest
        {
            Operation = BackupOperation.InitialSnapshot,
            CreatedAt = DateTimeOffset.Now.ToString("O"),
            AppVersion = appVersion,
            CodexVersion = CodexVersion,
            ChangedKeys = ["supplemental-baseline"],
        };
        await AddSnapshotFileAsync(directory, manifest, source, "content.toml", cancellationToken).ConfigureAwait(false);
        await WriteManifestAsync(directory, manifest, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ValidateSupplementalBaselineAsync(string directory, string expectedSource, CancellationToken cancellationToken)
    {
        BackupManifest manifest = JsonSerializer.Deserialize<BackupManifest>(
            await File.ReadAllBytesAsync(Path.Combine(directory, "manifest.json"), cancellationToken).ConfigureAwait(false),
            JsonOptions) ?? throw new InvalidDataException("Supplemental baseline manifest 为空。");
        BackupFileManifest file = manifest.Files.Count == 1
            ? manifest.Files[0]
            : throw new InvalidDataException("Supplemental baseline 文件清单无效。");
        if (!Path.GetFileName(Path.GetFullPath(directory)).Equals(PathIdentifier(expectedSource), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFullPath(file.OriginalPath).Equals(Path.GetFullPath(expectedSource), StringComparison.OrdinalIgnoreCase) ||
            !await ValidateHashesAsync(directory, manifest, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("Supplemental baseline 与目标路径或 SHA-256 不一致；已拒绝覆盖。 ");
        }
    }

    private static async Task AddSnapshotFileAsync(
        string directory,
        BackupManifest manifest,
        string source,
        string relativeName,
        CancellationToken cancellationToken)
    {
        TextFileSnapshot snapshot = await TextFileCodec.ReadAsync(source, cancellationToken).ConfigureAwait(false);
        manifest.Files.Add(new BackupFileManifest
        {
            RelativeName = relativeName,
            OriginalPath = Path.GetFullPath(source),
            Existed = snapshot.Fingerprint.Exists,
            Length = snapshot.Fingerprint.Length,
            Sha256 = snapshot.Fingerprint.Sha256,
            Utf8Bom = snapshot.Format.HasUtf8Bom,
            NewLine = snapshot.Format.NewLine == "\r\n" ? "CRLF" : "LF",
        });
        if (!snapshot.Fingerprint.Exists) return;
        string destination = ResolveSnapshotContentPath(directory, relativeName);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await File.WriteAllBytesAsync(destination, snapshot.Bytes, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteManifestAsync(string directory, BackupManifest manifest, CancellationToken cancellationToken)
    {
        byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        string temp = Path.Combine(directory, ".manifest-" + Guid.NewGuid().ToString("N") + ".tmp");
        await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await stream.WriteAsync(manifestBytes, cancellationToken).ConfigureAwait(false);
            stream.Flush(true);
        }

        File.Move(temp, Path.Combine(directory, "manifest.json"));
    }

    private static string ResolveSnapshotContentPath(string snapshotDirectory, string relativeName)
    {
        if (Path.IsPathRooted(relativeName)) throw new InvalidDataException("备份包含绝对存储路径。");
        string root = Path.GetFullPath(snapshotDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string candidate = Path.GetFullPath(Path.Combine(snapshotDirectory, relativeName));
        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("备份存储路径越界。");
        return candidate;
    }

    private string ResolveRestoreTarget(string home, BackupFileManifest file, bool isSupplemental)
    {
        if (!isSupplemental && file.RelativeName is ("config.toml" or "models.json")) return Path.Combine(home, file.RelativeName);
        if (!isSupplemental) throw new InvalidDataException($"备份包含不受支持的路径: {file.RelativeName}");
        if (file.RelativeName == "content.toml")
        {
            string baselineTarget = Path.GetFullPath(file.OriginalPath);
            ValidateSupplementalTarget(baselineTarget);
            return baselineTarget;
        }

        string supplementalPrefix = "supplemental" + Path.DirectorySeparatorChar;
        string normalized = file.RelativeName.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        if (!normalized.StartsWith(supplementalPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"备份包含不受支持的路径: {file.RelativeName}");
        }

        string target = Path.GetFullPath(file.OriginalPath);
        ValidateSupplementalTarget(target);
        string? storageDirectory = Path.GetDirectoryName(normalized);
        string expectedStorageName = PathIdentifier(target) + ".toml";
        if (!string.Equals(storageDirectory, "supplemental", StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(normalized).Equals(expectedStorageName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Supplemental backup 的路径标识与原始文件不一致。");
        }

        return target;
    }

    private void ValidateSupplementalTarget(string path)
    {
        string full = Path.GetFullPath(path);
        if (!full.EndsWith(".toml", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Supplemental backup 只允许 TOML 配置文件。");
        string backup = Path.GetFullPath(BackupRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (full.StartsWith(backup, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Supplemental target 不得位于备份目录内。");
    }

    private static string PathIdentifier(string path) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant())));

    private sealed record RestoreSource(
        BackupFileManifest File,
        string SnapshotDirectory,
        string StorageRelativeName,
        bool IsSupplemental);

    private static TextFileSnapshot DecodeSnapshot(string path, byte[] bytes)
    {
        bool bom = bytes.AsSpan().StartsWith(System.Text.Encoding.UTF8.Preamble);
        byte[] content = bom ? bytes[System.Text.Encoding.UTF8.Preamble.Length..] : bytes;
        string text = new System.Text.UTF8Encoding(false, true).GetString(content);
        return new TextFileSnapshot(path, bytes, text, TextFileCodec.DetectFormat(text, bom), FileFingerprint.Missing);
    }
}
