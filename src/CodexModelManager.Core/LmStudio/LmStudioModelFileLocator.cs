using System.Diagnostics;
using System.Text.Json;
using CodexModelManager.Core.Models;

namespace CodexModelManager.Core.LmStudio;

public static class LmStudioModelFileLocator
{
    private const int MaximumOutputCharacters = 4 * 1024 * 1024;

    public static async Task<string?> TryResolveAsync(
        ModelProfile model,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        string? key = model.SourceModelKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "lms.exe",
                Arguments = "ls --json",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
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
            if (process.ExitCode != 0 || output.Length == 0 || output.Length > MaximumOutputCharacters)
            {
                return null;
            }

            using JsonDocument document = JsonDocument.Parse(output);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            string[] paths = document.RootElement.EnumerateArray()
                .Where(item => item.TryGetProperty("modelKey", out JsonElement modelKey) && modelKey.GetString() == key)
                .Where(item => QuantizationMatches(item, model.Quantization))
                .Select(item => item.TryGetProperty("path", out JsonElement path) ? path.GetString() : null)
                .OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (paths.Length != 1)
            {
                return null;
            }

            return ResolveExistingPath(paths[0]);
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

    private static bool QuantizationMatches(JsonElement item, string? expected)
    {
        if (string.IsNullOrWhiteSpace(expected) || !item.TryGetProperty("quantization", out JsonElement quantization))
        {
            return true;
        }

        string? actual = quantization.ValueKind switch
        {
            JsonValueKind.String => quantization.GetString(),
            JsonValueKind.Object when quantization.TryGetProperty("name", out JsonElement name) => name.GetString(),
            _ => null,
        };
        return actual is null || actual.Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveExistingPath(string path)
    {
        if (Path.IsPathFullyQualified(path))
        {
            string full = Path.GetFullPath(path);
            return File.Exists(full) ? full : null;
        }

        string homeModels = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".lmstudio", "models");
        string candidate = Path.GetFullPath(Path.Combine(homeModels, path.Replace('/', Path.DirectorySeparatorChar)));
        string root = Path.GetFullPath(homeModels) + Path.DirectorySeparatorChar;
        return candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(candidate) ? candidate : null;
    }
}
