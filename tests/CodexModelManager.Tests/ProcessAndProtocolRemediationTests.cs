using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodexModelManager.Core.Codex;
using CodexModelManager.Core.Infrastructure;

namespace CodexModelManager.Tests;

public sealed class ProcessAndProtocolRemediationTests
{
    [Theory]
    [InlineData("1", 1, true)]
    [InlineData("\"1\"", 1, true)]
    [InlineData("\"01\"", 1, true)]
    [InlineData("2", 1, false)]
    [InlineData("\"other\"", 1, false)]
    [InlineData("null", 1, false)]
    public void AppServerResponseIdAcceptsOnlyMatchingNumberOrDecimalString(
        string json,
        int expected,
        bool matches)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Equal(matches, CodexAppServerClient.MatchesResponseId(document.RootElement, expected));
    }

    [Fact]
    public void AppServerModelNumericFieldsRejectWrongKindsWithoutInvalidOperationEscape()
    {
        using JsonDocument document = JsonDocument.Parse(
            "{\"data\":[{\"id\":\"fixture\",\"contextWindow\":\"not-a-number\"}]}");

        var models = CodexAppServerClient.ParseAppServerModels(document.RootElement);

        Assert.Single(models);
        Assert.Null(models[0].MaxContextLength);
        Assert.Null(models[0].LoadedContextLength);
    }

    [Fact]
    public void VerifiedNpmInvocationUsesNodeAndArgumentListWithoutShellConcatenation()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temporary = new TemporaryDirectory();
        string bin = Path.Combine(temporary.Path, "npm bin with spaces");
        string shim = Path.Combine(bin, "codex.cmd");
        string node = Path.Combine(bin, "node.exe");
        string entry = Path.Combine(bin, "node_modules", "@openai", "codex", "bin", "codex.js");
        Directory.CreateDirectory(Path.GetDirectoryName(entry)!);
        File.WriteAllText(shim, "@echo malicious shell text must never be interpreted");
        File.WriteAllText(node, string.Empty);
        File.WriteAllText(entry, "// fixture");

        CodexLaunchCommand command = Assert.IsType<CodexLaunchCommand>(
            CodexExecutableLocator.TryCreateNpmInvocation(shim, [bin]));
        ProcessStartInfo start = command.CreateStartInfo(["app-server", "--flag=value with spaces"]);

        Assert.Equal(Path.GetFullPath(node), command.FileName);
        Assert.Equal([Path.GetFullPath(entry)], command.PrefixArguments);
        Assert.False(start.UseShellExecute);
        Assert.Equal(
            [Path.GetFullPath(entry), "app-server", "--flag=value with spaces"],
            start.ArgumentList.Cast<string>());
    }

    [Fact]
    public async Task TestMcpServerSurvivesNonObjectJsonAndThenAnswersPing()
    {
        string serverDll = GetTestMcpServerDll();
        var start = new ProcessStartInfo(GetDotnetHost())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        start.ArgumentList.Add(serverDll);
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Failed to start Test MCP Server.");
        Task<string> stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            string[] malformed =
            [
                "[1,2]",
                "\"x\"",
                "42",
                "null",
                "true",
                "{\"id\":1,\"method\":42}",
                "{\"id\":2,\"method\":null}",
            ];
            foreach (string line in malformed)
            {
                await process.StandardInput.WriteLineAsync(line);
            }

            await process.StandardInput.WriteLineAsync(
                "{\"jsonrpc\":\"2.0\",\"id\":9,\"method\":\"tools/call\",\"params\":{\"name\":\"cmm_ping\",\"arguments\":{}}}");
            await process.StandardInput.FlushAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            string? response = await process.StandardOutput.ReadLineAsync(timeout.Token);

            Assert.NotNull(response);
            Assert.Contains("CMM_PONG", response, StringComparison.Ordinal);
            Assert.False(process.HasExited);
        }
        finally
        {
            await BoundedProcessCleanup.TerminateAndDrainAsync(process, [stderr], TimeSpan.FromSeconds(1));
        }
    }

    [Fact]
    public async Task CodexProcessReaderPreservesUtf8NonAsciiOutputIndependentOfParentCodePage()
    {
        string serverDll = GetTestMcpServerDll();
        var command = new CodexLaunchCommand(
            GetDotnetHost(),
            [serverDll, "--emit-utf8-fixture"],
            "UTF-8 test helper");
        using var temporary = new TemporaryDirectory();
        var client = new CodexAppServerClient(temporary.Path, command);

        string? output = await client.GetVersionAsync();

        Assert.Equal(@"C:\用户\模型.gguf", output);
    }

    [Fact]
    public async Task ProcessCleanupRemainsBoundedWhenAReaderNeverCompletes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string commandInterpreter = Environment.GetEnvironmentVariable("COMSPEC")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        var start = new ProcessStartInfo(commandInterpreter)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("/d");
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add("exit 0");
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Failed to start cleanup fixture.");
        Task neverCompletes = Task.Delay(Timeout.InfiniteTimeSpan);
        var stopwatch = Stopwatch.StartNew();

        await BoundedProcessCleanup.TerminateAndDrainAsync(
            process,
            [neverCompletes],
            TimeSpan.FromMilliseconds(100));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), stopwatch.Elapsed.ToString());
    }

    [Fact]
    public async Task ProcessCleanupToleratesAnAlreadyDisposedProcess()
    {
        var process = new Process();
        process.Dispose();

        await BoundedProcessCleanup.TerminateAndDrainAsync(
            process,
            [],
            TimeSpan.FromMilliseconds(50));
    }

    private static string GetTestMcpServerDll()
    {
        string configuration =
#if DEBUG
            "Debug";
#else
            "Release";
#endif
        string root = FindRepositoryRoot();
        string path = Path.Combine(
            root,
            "src",
            "CodexModelManager.TestMcpServer",
            "bin",
            configuration,
            "net8.0",
            "CodexModelManager.TestMcpServer.dll");
        Assert.True(File.Exists(path), $"Test MCP Server output missing: {path}");
        return path;
    }

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CodexModelManager.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Repository root was not found from the test output directory.");
    }

    private static string GetDotnetHost()
    {
        string executable = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string candidate = Path.Combine(directory.Trim('"'), executable);
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        throw new FileNotFoundException("dotnet host was not found on PATH.");
    }
}
