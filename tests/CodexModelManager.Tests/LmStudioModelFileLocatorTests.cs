using System.Text.Json;
using CodexModelManager.Core.LmStudio;
using CodexModelManager.Core.Models;

namespace CodexModelManager.Tests;

public sealed class LmStudioModelFileLocatorTests
{
    [Fact]
    public async Task LoadedProcessEvidenceResolvesCurrentUnslothModelWhenVariantsDoNotContainIt()
    {
        using var temporary = new TemporaryDirectory();
        string downloads = ConfigureDownloads(temporary.Path);
        const string relativePath = "unsloth/Qwen3.8-27B-GGUF/Qwen3.8-27B-UD-Q6_K_XL.gguf";
        string gguf = CreateFile(downloads, relativePath);
        var runner = new StubLmsCliCommandRunner(
            Success("[{\"model\":{\"modelKey\":\"qwen/qwen3.8-27b\"},\"variants\":[]} ]"),
            Success(CreatePsJson(relativePath)));
        var locator = new LmStudioModelFileLocator(runner, () => temporary.Path);

        LmStudioModelFileResolutionAttempt attempt = await locator.ResolveAsync(
            CreateLoadedModel(),
            new Uri("http://127.0.0.1:1234"));

        Assert.True(attempt.Succeeded, attempt.Diagnostic);
        Assert.Equal(LmStudioModelFileResolutionStatus.Success, attempt.Status);
        Assert.Equal(Path.GetFullPath(gguf), attempt.Resolution?.FilePath);
        Assert.Equal("lms ps --json", attempt.Resolution?.Source);
        Assert.Equal("qwen3.8-27b@q6_k_xl", attempt.Resolution?.SourceModelKey);
        Assert.Collection(
            runner.Calls,
            call => Assert.Equal(["ls", "--json", "--variants"], call),
            call => Assert.Equal(["ps", "--json", "--host", "127.0.0.1", "--port", "1234"], call));
    }

    [Fact]
    public async Task FullPathSourceWithMissingQuantizationResolvesExactNvfp4GgufFromProcessEvidence()
    {
        using var temporary = new TemporaryDirectory();
        string downloads = ConfigureDownloads(temporary.Path);
        const string relativePath = "esatapedico/Qwen3.8-27B-NVFP4-MTP-GGUF/Qwen3.8-27B-NVFP4-MTP-HIGHEST.gguf";
        const string nativeSource = "esatapedico/qwen3.8-27b-nvfp4-mtp-gguf/qwen3.8-27b-nvfp4-mtp-highest.gguf";
        string gguf = CreateFile(downloads, relativePath);
        ModelProfile model = CreateLoadedModel() with
        {
            Id = "qwen3.8-27b-nvfp4-mtp",
            LoadedInstanceId = "qwen3.8-27b-nvfp4-mtp",
            SourceModelKey = nativeSource,
            Quantization = null,
            LoadedContextLength = 262_144,
        };
        string processes = CreatePsJson(
            relativePath,
            modelKey: nativeSource,
            identifier: "qwen3.8-27b-nvfp4-mtp",
            publisher: "esatapedico",
            quantization: null,
            contextLength: 262_144,
            includeQuantization: false,
            indexedModelIdentifier: relativePath.Replace('/', '\\'));
        var runner = new StubLmsCliCommandRunner(Success("[]"), Success(processes));
        var locator = new LmStudioModelFileLocator(runner, () => temporary.Path);

        LmStudioModelFileResolutionAttempt attempt = await locator.ResolveAsync(
            model,
            new Uri("http://127.0.0.1:1234"));

        Assert.True(attempt.Succeeded, attempt.Diagnostic);
        Assert.Equal(Path.GetFullPath(gguf), attempt.Resolution?.FilePath);
        Assert.Equal(nativeSource, attempt.Resolution?.SourceModelKey);
        Assert.Null(attempt.Resolution?.Quantization);
        Assert.Equal("lms ps --json", attempt.Resolution?.Source);
        Assert.Equal(relativePath, attempt.Resolution?.ConcreteModelIdentifier);
    }

    [Fact]
    public void FullPathSourceKeepsSupportingLegacyLoadedIdModelKey()
    {
        using var temporary = new TemporaryDirectory();
        string downloads = Path.Combine(temporary.Path, "downloads");
        Directory.CreateDirectory(downloads);
        const string relativePath = "esatapedico/model/model.gguf";
        CreateFile(downloads, relativePath);
        string settings = JsonSerializer.Serialize(new { downloadsFolder = downloads });
        ModelProfile model = CreateLoadedModel() with
        {
            Id = "loaded-instance",
            LoadedInstanceId = "loaded-instance",
            SourceModelKey = relativePath,
            Quantization = null,
        };

        LmStudioModelFileResolutionAttempt attempt = LmStudioModelFileLocator.ResolvePsFromJson(
            model,
            CreatePsJson(relativePath, modelKey: "loaded-instance", identifier: "loaded-instance", publisher: "esatapedico", quantization: null, includeQuantization: false),
            settings,
            temporary.Path);

        Assert.True(attempt.Succeeded, attempt.Diagnostic);
        Assert.Equal(relativePath, attempt.Resolution?.ConcreteModelIdentifier);
    }

    [Fact]
    public void ProcessEvidenceSupportsPublisherQualifiedNativeSourceKey()
    {
        using var temporary = new TemporaryDirectory();
        string downloads = Path.Combine(temporary.Path, "downloads");
        Directory.CreateDirectory(downloads);
        const string relativePath = "unsloth/model.gguf";
        CreateFile(downloads, relativePath);
        string settings = JsonSerializer.Serialize(new { downloadsFolder = downloads });
        ModelProfile model = CreateLoadedModel() with
        {
            SourceModelKey = "unsloth/qwen3.8-27b@q6_k_xl",
        };

        LmStudioModelFileResolutionAttempt attempt = LmStudioModelFileLocator.ResolvePsFromJson(
            model,
            CreatePsJson(relativePath),
            settings,
            temporary.Path);

        Assert.True(attempt.Succeeded, attempt.Diagnostic);
        Assert.Equal("unsloth/qwen3.8-27b@q6_k_xl", attempt.Resolution?.SourceModelKey);
    }

    [Theory]
    [InlineData("modelKey")]
    [InlineData("identifier")]
    [InlineData("publisher")]
    [InlineData("source")]
    [InlineData("type")]
    [InlineData("architecture")]
    [InlineData("quantization")]
    [InlineData("context")]
    [InlineData("format")]
    public void ProcessEvidenceRejectsEveryNativeIdentityOrMetadataMismatch(string mismatch)
    {
        using var temporary = new TemporaryDirectory();
        string downloads = Path.Combine(temporary.Path, "downloads");
        Directory.CreateDirectory(downloads);
        string relativePath = mismatch == "publisher" ? "other/model.gguf" : "unsloth/model.gguf";
        CreateFile(downloads, relativePath);
        string settings = JsonSerializer.Serialize(new { downloadsFolder = downloads });
        ModelProfile model = CreateLoadedModel() with
        {
            SourceModelKey = mismatch is "publisher" or "source"
                ? "unsloth/qwen3.8-27b@q6_k_xl"
                : "qwen3.8-27b@q6_k_xl",
        };
        string json = CreatePsJson(
            relativePath,
            modelKey: mismatch == "modelKey" ? "wrong@q6_k_xl" : "qwen3.8-27b@q6_k_xl",
            identifier: mismatch == "identifier" ? "wrong@q6_k_xl" : "qwen3.8-27b@q6_k_xl",
            publisher: mismatch == "publisher" ? "other" : "unsloth",
            type: mismatch == "type" ? "embedding" : "llm",
            architecture: mismatch == "architecture" ? "wrong" : "qwen35",
            quantization: mismatch == "quantization" ? "Q4_K_M" : "Q6_K_XL",
            contextLength: mismatch == "context" ? 131_072 : 161_024,
            format: mismatch == "format" ? "mlx" : "gguf");
        if (mismatch == "source")
        {
            model = model with { SourceModelKey = "other/qwen3.8-27b@q6_k_xl" };
        }

        LmStudioModelFileResolutionAttempt attempt = LmStudioModelFileLocator.ResolvePsFromJson(
            model,
            json,
            settings,
            temporary.Path);

        Assert.Equal(LmStudioModelFileResolutionStatus.IdentityMismatch, attempt.Status);
        Assert.Null(attempt.Resolution);
    }

    [Fact]
    public void ProcessEvidenceRequiresQuantizationToBeMissingOrPresentOnBothSurfaces()
    {
        using var temporary = new TemporaryDirectory();
        string downloads = Path.Combine(temporary.Path, "downloads");
        Directory.CreateDirectory(downloads);
        CreateFile(downloads, "unsloth/model.gguf");
        string settings = JsonSerializer.Serialize(new { downloadsFolder = downloads });

        LmStudioModelFileResolutionAttempt nativeOnly = LmStudioModelFileLocator.ResolvePsFromJson(
            CreateLoadedModel(),
            CreatePsJson("unsloth/model.gguf", quantization: null, includeQuantization: false),
            settings,
            temporary.Path);
        LmStudioModelFileResolutionAttempt cliOnly = LmStudioModelFileLocator.ResolvePsFromJson(
            CreateLoadedModel() with { Quantization = null },
            CreatePsJson("unsloth/model.gguf"),
            settings,
            temporary.Path);

        Assert.Equal(LmStudioModelFileResolutionStatus.IdentityMismatch, nativeOnly.Status);
        Assert.Contains("quantization", nativeOnly.Diagnostic, StringComparison.Ordinal);
        Assert.Equal(LmStudioModelFileResolutionStatus.IdentityMismatch, cliOnly.Status);
        Assert.Contains("quantization", cliOnly.Diagnostic, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData("Q6_K_XL", null, false)]
    [InlineData(null, "Q6_K_XL", false)]
    [InlineData("Q6_K_XL", "Q6_K_XL", true)]
    [InlineData("Q6_K_XL", "Q4_K_M", false)]
    public void VariantEvidenceUsesTheSameNullableExactQuantizationRule(
        string? nativeQuantization,
        string? cliQuantization,
        bool shouldResolve)
    {
        using var temporary = new TemporaryDirectory();
        string downloads = Path.Combine(temporary.Path, "downloads");
        Directory.CreateDirectory(downloads);
        const string relativePath = "unsloth/model.gguf";
        CreateFile(downloads, relativePath);
        string settings = JsonSerializer.Serialize(new { downloadsFolder = downloads });
        var candidate = new Dictionary<string, object?>
        {
            ["modelKey"] = "qwen3.8-27b@q6_k_xl",
            ["path"] = relativePath,
            ["architecture"] = "qwen35",
            ["quantization"] = cliQuantization is null ? null : new { name = cliQuantization },
        };
        string variants = JsonSerializer.Serialize(new[] { candidate });

        LmStudioModelFileResolution? resolution = LmStudioModelFileLocator.ResolveFromJson(
            CreateLoadedModel() with { Quantization = nativeQuantization },
            variants,
            settings,
            temporary.Path);

        if (shouldResolve)
        {
            Assert.NotNull(resolution);
            Assert.Equal(Path.GetFullPath(Path.Combine(downloads, relativePath)), resolution.FilePath);
        }
        else
        {
            Assert.Null(resolution);
        }
    }

    [Fact]
    public void FullPathSourceRejectsConflictingPathIdentifiers()
    {
        using var temporary = new TemporaryDirectory();
        string downloads = Path.Combine(temporary.Path, "downloads");
        Directory.CreateDirectory(downloads);
        const string source = "esatapedico/model/model.gguf";
        CreateFile(downloads, source);
        string settings = JsonSerializer.Serialize(new { downloadsFolder = downloads });
        ModelProfile model = CreateLoadedModel() with
        {
            SourceModelKey = source,
            Quantization = null,
        };
        string processes = CreatePsJson(
            source,
            publisher: "esatapedico",
            quantization: null,
            includeQuantization: false,
            indexedModelIdentifier: "esatapedico/other/model.gguf");

        LmStudioModelFileResolutionAttempt attempt = LmStudioModelFileLocator.ResolvePsFromJson(
            model,
            processes,
            settings,
            temporary.Path);

        Assert.Equal(LmStudioModelFileResolutionStatus.IdentityMismatch, attempt.Status);
        Assert.Contains("source/path/indexedModelIdentifier", attempt.Diagnostic, StringComparison.Ordinal);
        Assert.Null(attempt.Resolution);
    }

    [Fact]
    public void FullPathSourceRejectsPresentNonPathIndexedIdentifier()
    {
        using var temporary = new TemporaryDirectory();
        string downloads = Path.Combine(temporary.Path, "downloads");
        Directory.CreateDirectory(downloads);
        const string source = "esatapedico/model/model.gguf";
        CreateFile(downloads, source);
        string settings = JsonSerializer.Serialize(new { downloadsFolder = downloads });
        ModelProfile model = CreateLoadedModel() with
        {
            SourceModelKey = source,
            Quantization = null,
        };
        string processes = CreatePsJson(
            source,
            publisher: "esatapedico",
            quantization: null,
            includeQuantization: false,
            indexedModelIdentifier: "qwen3.8-27b@loaded-instance");

        LmStudioModelFileResolutionAttempt attempt = LmStudioModelFileLocator.ResolvePsFromJson(
            model,
            processes,
            settings,
            temporary.Path);

        Assert.Equal(LmStudioModelFileResolutionStatus.IdentityMismatch, attempt.Status);
        Assert.Contains("source/path/indexedModelIdentifier", attempt.Diagnostic, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    public void FullPathSourceRequiresAtLeastOneExactCliPathIdentifier(
        bool includePath,
        bool includeIndexedModelIdentifier,
        bool expectedSuccess)
    {
        using var temporary = new TemporaryDirectory();
        string downloads = Path.Combine(temporary.Path, "downloads");
        Directory.CreateDirectory(downloads);
        const string source = "esatapedico/model/model.gguf";
        CreateFile(downloads, source);
        string settings = JsonSerializer.Serialize(new { downloadsFolder = downloads });
        ModelProfile model = CreateLoadedModel() with
        {
            SourceModelKey = source,
            Quantization = null,
        };
        string processes = CreatePsJson(
            source,
            publisher: "esatapedico",
            quantization: null,
            includeQuantization: false,
            includePath: includePath,
            includeIndexedModelIdentifier: includeIndexedModelIdentifier);

        LmStudioModelFileResolutionAttempt attempt = LmStudioModelFileLocator.ResolvePsFromJson(
            model,
            processes,
            settings,
            temporary.Path);

        Assert.Equal(expectedSuccess, attempt.Succeeded);
        Assert.Equal(
            expectedSuccess ? LmStudioModelFileResolutionStatus.Success : LmStudioModelFileResolutionStatus.IdentityMismatch,
            attempt.Status);
    }

    [Theory]
    [InlineData("traversal", LmStudioModelFileResolutionStatus.UnsafePath)]
    [InlineData("publisher", LmStudioModelFileResolutionStatus.UnsafePath)]
    [InlineData("missing", LmStudioModelFileResolutionStatus.MissingFile)]
    [InlineData("extension", LmStudioModelFileResolutionStatus.UnsupportedFileType)]
    public void ProcessEvidenceRejectsUnsafeMissingOrNonGgufPaths(
        string shape,
        LmStudioModelFileResolutionStatus expectedStatus)
    {
        using var temporary = new TemporaryDirectory();
        string downloads = Path.Combine(temporary.Path, "downloads");
        Directory.CreateDirectory(downloads);
        string relativePath = shape switch
        {
            "traversal" => "unsloth/../../escape.gguf",
            "publisher" => "other/model.gguf",
            "extension" => "unsloth/model.bin",
            _ => "unsloth/missing.gguf",
        };
        if (shape is "publisher" or "extension")
        {
            CreateFile(downloads, relativePath);
        }

        string settings = JsonSerializer.Serialize(new { downloadsFolder = downloads });
        LmStudioModelFileResolutionAttempt attempt = LmStudioModelFileLocator.ResolvePsFromJson(
            CreateLoadedModel(),
            CreatePsJson(relativePath),
            settings,
            temporary.Path);

        Assert.Equal(expectedStatus, attempt.Status);
        Assert.Null(attempt.Resolution);
    }

    [Fact]
    public void ProcessEvidenceRejectsMultipleDifferentExistingCandidates()
    {
        using var temporary = new TemporaryDirectory();
        string downloads = Path.Combine(temporary.Path, "downloads");
        Directory.CreateDirectory(downloads);
        CreateFile(downloads, "unsloth/one.gguf");
        CreateFile(downloads, "unsloth/two.gguf");
        string settings = JsonSerializer.Serialize(new { downloadsFolder = downloads });
        string first = CreatePsObject("unsloth/one.gguf");
        string second = CreatePsObject("unsloth/two.gguf");

        LmStudioModelFileResolutionAttempt attempt = LmStudioModelFileLocator.ResolvePsFromJson(
            CreateLoadedModel(),
            $"[{first},{second}]",
            settings,
            temporary.Path);

        Assert.Equal(LmStudioModelFileResolutionStatus.Ambiguous, attempt.Status);
        Assert.Null(attempt.Resolution);
    }

    [Fact]
    public async Task ConflictingValidLsAndPsPathsFailClosed()
    {
        using var temporary = new TemporaryDirectory();
        string downloads = ConfigureDownloads(temporary.Path);
        string firstPath = CreateFile(downloads, "publisher/one.gguf");
        string secondPath = CreateFile(downloads, "publisher/two.gguf");
        string variants = JsonSerializer.Serialize(new[]
        {
            new
            {
                modelKey = "publisher/model@q8_0",
                path = firstPath,
                architecture = "qwen35",
                quantization = new { name = "Q8_0" },
            },
        });
        string processes = CreatePsJson(
            "publisher/two.gguf",
            modelKey: "model@q8_0",
            identifier: "model@q8_0",
            publisher: "publisher",
            quantization: "Q8_0",
            contextLength: 32_768);
        var runner = new StubLmsCliCommandRunner(Success(variants), Success(processes));
        var locator = new LmStudioModelFileLocator(runner, () => temporary.Path);
        ModelProfile model = CreateLoadedModel() with
        {
            Id = "model@q8_0",
            LoadedInstanceId = "model@q8_0",
            SourceModelKey = "publisher/model@q8_0",
            Quantization = "Q8_0",
            LoadedContextLength = 32_768,
        };

        LmStudioModelFileResolutionAttempt attempt = await locator.ResolveAsync(
            model,
            new Uri("http://localhost:1234"));

        Assert.Equal(LmStudioModelFileResolutionStatus.Conflict, attempt.Status);
        Assert.Null(attempt.Resolution);
    }

    [Fact]
    public async Task InvalidJsonBlocksEvenWhenOtherSurfaceHasAValidPath()
    {
        using var temporary = new TemporaryDirectory();
        string downloads = ConfigureDownloads(temporary.Path);
        CreateFile(downloads, "unsloth/model.gguf");
        var runner = new StubLmsCliCommandRunner(
            Success("{not-json"),
            Success(CreatePsJson("unsloth/model.gguf")));
        var locator = new LmStudioModelFileLocator(runner, () => temporary.Path);

        LmStudioModelFileResolutionAttempt attempt = await locator.ResolveAsync(
            CreateLoadedModel(),
            new Uri("http://127.0.0.1:1234"));

        Assert.Equal(LmStudioModelFileResolutionStatus.InvalidJson, attempt.Status);
        Assert.Null(attempt.Resolution);
        Assert.DoesNotContain("not-json", attempt.Diagnostic, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OneFailedCliSurfaceCannotBeHiddenByAValidOtherSurface(bool variantsFails)
    {
        using var temporary = new TemporaryDirectory();
        string downloads = ConfigureDownloads(temporary.Path);
        CreateFile(downloads, "unsloth/model.gguf");
        LmsCliCommandResult failed = new(LmsCliCommandStatus.TimedOut, null);
        LmsCliCommandResult variants = variantsFails
            ? failed
            : Success("[{\"model\":{\"modelKey\":\"unrelated/model\"},\"variants\":[]}]");
        LmsCliCommandResult processes = variantsFails
            ? Success(CreatePsJson("unsloth/model.gguf"))
            : failed;
        var runner = new StubLmsCliCommandRunner(variants, processes);
        var locator = new LmStudioModelFileLocator(runner, () => temporary.Path);

        LmStudioModelFileResolutionAttempt attempt = await locator.ResolveAsync(
            CreateLoadedModel(),
            new Uri("http://127.0.0.1:1234"));

        Assert.Equal(LmStudioModelFileResolutionStatus.CliTimedOut, attempt.Status);
        Assert.Null(attempt.Resolution);
        Assert.DoesNotContain("unsloth/model.gguf", attempt.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("TimedOut", LmStudioModelFileResolutionStatus.CliTimedOut)]
    [InlineData("Failed", LmStudioModelFileResolutionStatus.CliFailed)]
    [InlineData("OutputTooLarge", LmStudioModelFileResolutionStatus.CliFailed)]
    public async Task CliFailuresReturnStableSanitizedCategories(
        string commandStatusName,
        LmStudioModelFileResolutionStatus expectedStatus)
    {
        using var temporary = new TemporaryDirectory();
        ConfigureDownloads(temporary.Path);
        var result = new LmsCliCommandResult(Enum.Parse<LmsCliCommandStatus>(commandStatusName), null);
        var runner = new StubLmsCliCommandRunner(result, result);
        var locator = new LmStudioModelFileLocator(runner, () => temporary.Path);

        LmStudioModelFileResolutionAttempt attempt = await locator.ResolveAsync(
            CreateLoadedModel(),
            new Uri("http://127.0.0.1:1234"));

        Assert.Equal(expectedStatus, attempt.Status);
        Assert.Null(attempt.Resolution);
        Assert.DoesNotContain("secret", attempt.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stderr", attempt.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MissingCliReturnsUnavailableWithoutRawProcessDetails()
    {
        using var temporary = new TemporaryDirectory();
        ConfigureDownloads(temporary.Path);
        var runner = new StubLmsCliCommandRunner(new LmsCliCommandResult(LmsCliCommandStatus.Unavailable, null));
        var locator = new LmStudioModelFileLocator(runner, () => temporary.Path);

        LmStudioModelFileResolutionAttempt attempt = await locator.ResolveAsync(
            CreateLoadedModel(),
            new Uri("http://127.0.0.1:1234"));

        Assert.Equal(LmStudioModelFileResolutionStatus.CliUnavailable, attempt.Status);
        Assert.Single(runner.Calls);
        Assert.Null(attempt.Resolution);
    }

    [Fact]
    public async Task RemoteEndpointAndInvalidSettingsStopBeforeCliExecution()
    {
        using var temporary = new TemporaryDirectory();
        var runner = new StubLmsCliCommandRunner();
        var locator = new LmStudioModelFileLocator(runner, () => temporary.Path);

        LmStudioModelFileResolutionAttempt remote = await locator.ResolveAsync(
            CreateLoadedModel(),
            new Uri("https://example.invalid:1234"));

        Assert.Equal(LmStudioModelFileResolutionStatus.UnsupportedEndpoint, remote.Status);
        Assert.Empty(runner.Calls);

        string settingsDirectory = Path.Combine(temporary.Path, ".lmstudio");
        Directory.CreateDirectory(settingsDirectory);
        File.WriteAllText(Path.Combine(settingsDirectory, "settings.json"), "{invalid");
        LmStudioModelFileResolutionAttempt invalidSettings = await locator.ResolveAsync(
            CreateLoadedModel(),
            new Uri("http://127.0.0.1:1234"));
        Assert.Equal(LmStudioModelFileResolutionStatus.InvalidSettings, invalidSettings.Status);
        Assert.Empty(runner.Calls);

        File.WriteAllText(Path.Combine(settingsDirectory, "settings.json"), "{\"downloadsFolder\":42}");
        LmStudioModelFileResolutionAttempt wrongSettingsType = await locator.ResolveAsync(
            CreateLoadedModel(),
            new Uri("http://127.0.0.1:1234"));
        Assert.Equal(LmStudioModelFileResolutionStatus.InvalidSettings, wrongSettingsType.Status);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task IncompleteNativeSnapshotStopsBeforeCliExecution()
    {
        using var temporary = new TemporaryDirectory();
        var runner = new StubLmsCliCommandRunner();
        var locator = new LmStudioModelFileLocator(runner, () => temporary.Path);

        LmStudioModelFileResolutionAttempt attempt = await locator.ResolveAsync(
            CreateLoadedModel() with { LoadedContextLength = null },
            new Uri("http://127.0.0.1:1234"));

        Assert.Equal(LmStudioModelFileResolutionStatus.InvalidModelSnapshot, attempt.Status);
        Assert.Contains("实际 context", attempt.Diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("quantization", attempt.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(runner.Calls);
    }

    private static string ConfigureDownloads(string userProfile)
    {
        string downloads = Path.Combine(userProfile, "downloads");
        Directory.CreateDirectory(downloads);
        string settingsDirectory = Path.Combine(userProfile, ".lmstudio");
        Directory.CreateDirectory(settingsDirectory);
        File.WriteAllText(
            Path.Combine(settingsDirectory, "settings.json"),
            JsonSerializer.Serialize(new { downloadsFolder = downloads }));
        return downloads;
    }

    private static string CreateFile(string root, string relativePath)
    {
        string path = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "fixture");
        return path;
    }

    private static ModelProfile CreateLoadedModel() => new(
        "qwen3.8-27b@q6_k_xl",
        "Qwen3.8 27B UD",
        ProviderKind.LmStudio,
        Quantization: "Q6_K_XL",
        IsLoaded: true,
        MaxContextLength: 262_144,
        LoadedContextLength: 161_024,
        LoadedInstanceId: "qwen3.8-27b@q6_k_xl",
        Architecture: "qwen35",
        ModelType: "llm",
        SourceModelKey: "qwen3.8-27b@q6_k_xl",
        Format: "gguf");

    private static string CreatePsJson(
        string relativePath,
        string modelKey = "qwen3.8-27b@q6_k_xl",
        string identifier = "qwen3.8-27b@q6_k_xl",
        string publisher = "unsloth",
        string type = "llm",
        string architecture = "qwen35",
        string? quantization = "Q6_K_XL",
        int contextLength = 161_024,
        string format = "gguf",
        bool includeQuantization = true,
        string? indexedModelIdentifier = null,
        bool includePath = true,
        bool includeIndexedModelIdentifier = true) =>
        $"[{CreatePsObject(relativePath, modelKey, identifier, publisher, type, architecture, quantization, contextLength, format, includeQuantization, indexedModelIdentifier, includePath, includeIndexedModelIdentifier)}]";

    private static string CreatePsObject(
        string relativePath,
        string modelKey = "qwen3.8-27b@q6_k_xl",
        string identifier = "qwen3.8-27b@q6_k_xl",
        string publisher = "unsloth",
        string type = "llm",
        string architecture = "qwen35",
        string? quantization = "Q6_K_XL",
        int contextLength = 161_024,
        string format = "gguf",
        bool includeQuantization = true,
        string? indexedModelIdentifier = null,
        bool includePath = true,
        bool includeIndexedModelIdentifier = true)
    {
        var value = new Dictionary<string, object?>
        {
            ["type"] = type,
            ["modelKey"] = modelKey,
            ["format"] = format,
            ["publisher"] = publisher,
            ["architecture"] = architecture,
            ["identifier"] = identifier,
            ["maxContextLength"] = 262_144,
            ["contextLength"] = contextLength,
        };
        if (includePath)
        {
            value["path"] = relativePath;
        }

        if (includeIndexedModelIdentifier)
        {
            value["indexedModelIdentifier"] = indexedModelIdentifier ?? relativePath;
        }

        if (includeQuantization)
        {
            value["quantization"] = quantization is null ? null : new { name = quantization };
        }

        return JsonSerializer.Serialize(value);
    }

    private static LmsCliCommandResult Success(string json) => new(LmsCliCommandStatus.Success, json);

    private sealed class StubLmsCliCommandRunner(params LmsCliCommandResult[] results) : ILmsCliCommandRunner
    {
        private readonly Queue<LmsCliCommandResult> queued = new(results);

        public List<string[]> Calls { get; } = [];

        public Task<LmsCliCommandResult> RunAsync(
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(arguments.ToArray());
            if (queued.Count == 0)
            {
                throw new InvalidOperationException("Unexpected lms CLI invocation.");
            }

            return Task.FromResult(queued.Dequeue());
        }
    }
}
