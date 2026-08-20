using System.Diagnostics;
using System.Text.Json;
using CodexModelManager.Core.Models;

namespace CodexModelManager.Core.LmStudio;

public static class LmStudioModelFileLocator
{
    private const int MaximumOutputCharacters = 4 * 1024 * 1024;

    public static async Task<string?> TryResolveAsync(
        ModelProfile model,
        CancellationToken cancellationToken = default) =>
        (await TryResolveDetailedAsync(model, cancellationToken).ConfigureAwait(false))?.FilePath;

    public static async Task<LmStudioModelFileResolution?> TryResolveDetailedAsync(
        ModelProfile model,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (string.IsNullOrWhiteSpace(model.SourceModelKey))
        {
            return null;
        }

        try
        {
            string? output = await RunLmsVariantsAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(output))
            {
                return null;
            }

            string userProfile = ResolveUserProfile();
            string settingsPath = Path.Combine(userProfile, ".lmstudio", "settings.json");
            string? settingsJson = File.Exists(settingsPath)
                ? await File.ReadAllTextAsync(settingsPath, cancellationToken).ConfigureAwait(false)
                : null;
            return ResolveFromJson(model, output, settingsJson, userProfile);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or JsonException or OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return null;
        }
    }

    public static LmStudioModelFileResolution? ResolveFromJson(
        ModelProfile model,
        string variantsJson,
        string? settingsJson,
        string userProfile)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(variantsJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(userProfile);
        string? sourceKey = model.SourceModelKey;
        if (string.IsNullOrWhiteSpace(sourceKey))
        {
            return null;
        }

        using JsonDocument document = JsonDocument.Parse(variantsJson);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        List<CliCandidate> candidates = [];
        foreach (JsonElement item in document.RootElement.EnumerateArray())
        {
            if (item.TryGetProperty("model", out JsonElement wrapperModel) && wrapperModel.ValueKind == JsonValueKind.Object)
            {
                if (!StringEquals(GetString(wrapperModel, "modelKey"), sourceKey))
                {
                    continue;
                }

                string? selectedVariant = model.SelectedVariant ?? GetString(wrapperModel, "selectedVariant");
                if (item.TryGetProperty("variants", out JsonElement variants) && variants.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement variant in variants.EnumerateArray())
                    {
                        AddCandidate(candidates, variant, sourceKey, selectedVariant, model, "lms ls --json --variants");
                    }
                }

                if (string.IsNullOrWhiteSpace(selectedVariant))
                {
                    AddCandidate(candidates, wrapperModel, sourceKey, null, model, "lms ls --json --variants:model");
                }

                continue;
            }

            AddCandidate(candidates, item, sourceKey, model.SelectedVariant, model, "lms ls --json --variants:legacy");
        }

        string downloadsRoot = ReadDownloadsRoot(settingsJson, userProfile);
        LmStudioModelFileResolution[] resolved = candidates
            .Select(candidate => ResolveCandidate(candidate, downloadsRoot, userProfile))
            .OfType<LmStudioModelFileResolution>()
            .DistinctBy(candidate => candidate.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return resolved.Length == 1 ? resolved[0] : null;
    }

    private static void AddCandidate(
        List<CliCandidate> candidates,
        JsonElement item,
        string sourceKey,
        string? selectedVariant,
        ModelProfile model,
        string source)
    {
        string? modelKey = GetString(item, "modelKey");
        if (string.IsNullOrWhiteSpace(modelKey))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(selectedVariant))
        {
            if (!StringEquals(modelKey, selectedVariant))
            {
                return;
            }
        }
        else if (!StringEquals(modelKey, sourceKey))
        {
            return;
        }

        string? quantization = GetQuantization(item);
        string? architecture = GetString(item, "architecture");
        if (!Compatible(model.Quantization, quantization) || !Compatible(model.Architecture, architecture))
        {
            return;
        }

        candidates.Add(new CliCandidate(
            sourceKey,
            selectedVariant ?? (StringEquals(modelKey, sourceKey) ? null : modelKey),
            architecture,
            quantization,
            GetString(item, "path"),
            GetString(item, "indexedModelIdentifier"),
            source));
    }

    private static LmStudioModelFileResolution? ResolveCandidate(
        CliCandidate candidate,
        string downloadsRoot,
        string userProfile)
    {
        string? path = ResolveExistingAbsolute(candidate.Path);
        if (path is null)
        {
            string? indexedPath = ExtractIndexedRelativePath(candidate.IndexedModelIdentifier);
            if (indexedPath is not null)
            {
                path = ResolveUnderRoot(downloadsRoot, indexedPath);
            }
        }

        if (path is null && !string.IsNullOrWhiteSpace(candidate.Path))
        {
            path = ResolveUnderRoot(downloadsRoot, candidate.Path);
            path ??= ResolveUnderRoot(Path.Combine(userProfile, ".lmstudio", "models"), candidate.Path);
        }

        if (path is null || !Path.GetExtension(path).Equals(".gguf", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new LmStudioModelFileResolution(
            path,
            candidate.SourceModelKey,
            candidate.SelectedVariant,
            candidate.Architecture,
            candidate.Quantization,
            candidate.Source);
    }

    private static string ReadDownloadsRoot(string? settingsJson, string userProfile)
    {
        if (!string.IsNullOrWhiteSpace(settingsJson))
        {
            using JsonDocument document = JsonDocument.Parse(settingsJson);
            string? configured = GetString(document.RootElement, "downloadsFolder");
            if (!string.IsNullOrWhiteSpace(configured) && Path.IsPathFullyQualified(configured))
            {
                return Path.GetFullPath(configured);
            }
        }

        return Path.GetFullPath(Path.Combine(userProfile, ".lmstudio", "models"));
    }

    private static string ResolveUserProfile()
    {
        string? environmentProfile = Environment.GetEnvironmentVariable("USERPROFILE");
        if (!string.IsNullOrWhiteSpace(environmentProfile) && Path.IsPathFullyQualified(environmentProfile))
        {
            return Path.GetFullPath(environmentProfile);
        }

        return Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    private static string? ExtractIndexedRelativePath(string? identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return null;
        }

        int separator = identifier.IndexOf('@');
        string relative = separator >= 0 ? identifier[(separator + 1)..] : identifier;
        return relative.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) ? relative : null;
    }

    private static string? ResolveExistingAbsolute(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return null;
        }

        string full = Path.GetFullPath(path);
        return File.Exists(full) ? full : null;
    }

    private static string? ResolveUnderRoot(string root, string relative)
    {
        if (Path.IsPathFullyQualified(relative))
        {
            return ResolveExistingAbsolute(relative);
        }

        string fullRoot = Path.GetFullPath(root);
        string candidate = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        string prefix = fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && File.Exists(candidate) ? candidate : null;
    }

    private static async Task<string?> RunLmsVariantsAsync(CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "lms.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("ls");
        startInfo.ArgumentList.Add("--json");
        startInfo.ArgumentList.Add("--variants");
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            return null;
        }

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryTerminate(process);
            await WaitForExitAfterTerminationAsync(process).ConfigureAwait(false);
            await ObserveOutputAsync(stdoutTask, stderrTask).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }

        string output = await stdoutTask.ConfigureAwait(false);
        _ = await stderrTask.ConfigureAwait(false);
        return process.ExitCode == 0 && output.Length is > 0 and <= MaximumOutputCharacters ? output : null;
    }

    private static void TryTerminate(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
        }
    }

    private static async Task WaitForExitAfterTerminationAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static async Task ObserveOutputAsync(params Task<string>[] tasks)
    {
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException)
        {
        }
    }

    private static bool Compatible(string? expected, string? actual) =>
        string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(actual) || expected.Equals(actual, StringComparison.OrdinalIgnoreCase);

    private static bool StringEquals(string? left, string? right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? GetQuantization(JsonElement element)
    {
        if (!element.TryGetProperty("quantization", out JsonElement value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Object => GetString(value, "name"),
            _ => null,
        };
    }

    private sealed record CliCandidate(
        string SourceModelKey,
        string? SelectedVariant,
        string? Architecture,
        string? Quantization,
        string? Path,
        string? IndexedModelIdentifier,
        string Source);
}
