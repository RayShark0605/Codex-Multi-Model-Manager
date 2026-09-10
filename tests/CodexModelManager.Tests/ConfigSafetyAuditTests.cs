using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexModelManager.Core.Abstractions;
using CodexModelManager.Core.Backup;
using CodexModelManager.Core.Codex;
using CodexModelManager.Core.Infrastructure;
using CodexModelManager.Core.Models;

namespace CodexModelManager.Tests;

/// <summary>ConfigSafetyAudit 相关测试集。</summary>
public sealed class ConfigSafetyAuditTests
{
    [Fact]
    public void ManagedRemovalPreservesQuotedDottedProviderSiblings()
    {
        const string neighbor = "[model_providers.\"deepseek.owned-by-user\"]\nname = \"keep\"\n";
        const string actualManaged = "[model_providers.deepseek]\nname = \"remove\"\n[model_providers.deepseek.auth]\ncommand = \"remove\"\n";
        var engine = new TomlConfigPatchEngine();
        string result = engine.Apply("model = \"old\"\n" + actualManaged + neighbor,
            new ConfigPatchRequest(new Dictionary<string, string?> { ["model"] = "\"new\"" }, new Dictionary<string, string?>(), ["model_providers.deepseek"])).Text;

        Assert.Equal("model = \"new\"\n" + neighbor, result);
        Assert.Contains("model_providers.\"deepseek.owned-by-user\"", engine.Read(result).TableBodies.Keys);
    }

    [Theory]
    [InlineData("# \"\"\" example")]
    [InlineData("# ''' example")]
    [InlineData("note = '\"\"\" example'")]
    public void QuoteTextDoesNotHideTheNextUnknownTable(string quotedText)
    {
        const string neighbor = "[future.unknown]\nmagic = 7\n";
        string original = "model = \"old\"\n[model_providers.deepseek]\n" + quotedText + "\n" + neighbor;
        var engine = new TomlConfigPatchEngine();
        string result = engine.Apply(original, new ConfigPatchRequest(new Dictionary<string, string?>(), new Dictionary<string, string?>(), ["model_providers.deepseek"])).Text;

        Assert.Equal("model = \"old\"\n" + neighbor, result);
    }

    [Theory]
    [InlineData("\n", "\"\"\"old\nline\"\"\"")]
    [InlineData("\r\n", "'''old\r\nline'''")]
    public void MultilineRootValueUsesItsEntireSpanAndPreservesOtherBytes(string newline, string rawValue)
    {
        string prefix = "# \"\"\" comment" + newline + "  'model'  = ";
        string suffix = " # keep" + newline + "[future.unknown]" + newline + "payload = [1, 2]";
        string original = prefix + rawValue + suffix;
        var engine = new TomlConfigPatchEngine();
        ConfigPatchResult result = engine.Apply(original, new ConfigPatchRequest(new Dictionary<string, string?> { ["model"] = "\"new\"" }, new Dictionary<string, string?>()));

        Assert.Equal(prefix + "\"new\"" + suffix, result.Text);
        Assert.Equal(rawValue, engine.Read(original).RootValues["model"]);
        byte[] encoded = TextFileCodec.Encode(result.Text, TextFileCodec.DetectFormat(original, hasBom: true));
        Assert.True(encoded.AsSpan().StartsWith(Encoding.UTF8.Preamble));
    }

    [Fact]
    public void EscapedTomlKeyAndLiteralDotsRemainDistinct()
    {
        const string original = "\"\\U0000006Dodel\" = \"old\"\n\"model.literal\" = \"keep\"\n";
        var engine = new TomlConfigPatchEngine();
        string result = engine.Apply(original, new ConfigPatchRequest(new Dictionary<string, string?> { ["model"] = "\"new\"" }, new Dictionary<string, string?>())).Text;
        Assert.Equal(original.Replace("\"old\"", "\"new\"", StringComparison.Ordinal), result);
    }

    [Theory]
    [InlineData("\"\"\"\nopenai\"\"\"", "openai")]
    [InlineData("'''\nopenai'''", "openai")]
    [InlineData("\"\\U0000006Fpenai\"", "openai")]
    [InlineData("'gpt-model'", "gpt-model")]
    [InlineData("unquoted-model", "unquoted-model")]
    [InlineData("\"invalid\\q\"", "\"invalid\\q\"")]
    public void RuntimeUnquoteUsesTomlStringSemanticsAndToleratesOtherInput(string raw, string expected)
    {
        Assert.Equal(expected, CodexRuntimeProbe.Unquote(raw));
    }

    [Fact]
    public async Task MultilineProviderTokenSurvivesProviderStateRoundTrip()
    {
        const string providerToken = "\"\"\"\nopenai\"\"\"";
        using var harness = new SwitchHarness("model = \"gpt-native\"\nmodel_provider = " + providerToken + " # preserve\n");
        SwitchPlan local = await harness.Service.CreatePlanAsync(harness.Request(ProviderKind.LmStudio));
        Assert.Equal(ProviderKind.OpenAI, local.SourceProvider);
        await harness.Service.CommitAsync(local);
        await harness.Service.CommitAsync(await harness.Service.CreatePlanAsync(harness.Request(ProviderKind.OpenAI)));
        Assert.Equal(providerToken, harness.Patch.Read(harness.ReadConfig()).RootValues["model_provider"]);
        Assert.Equal("openai", CodexRuntimeProbe.Unquote(harness.Patch.Read(harness.ReadConfig()).RootValues["model_provider"]));
    }

    [Fact]
    public async Task QuotedClosingBracketSecondarySelectionOnlyChangesItsOwnValue()
    {
        using var temporary = new TemporaryDirectory();
        string path = Path.Combine(temporary.Path, "config.toml");
        const string text = "[profiles.safe]\nmodel = \"one\"\n[profiles.\"literal]name\"]\nmodel = \"two\"\n";
        await File.WriteAllTextAsync(path, text);
        IReadOnlyList<SecondaryModelOverride> found = await new SecondaryModelOverrideScanner(new TomlConfigPatchEngine()).ScanAsync(path);
        Assert.Equal(["profiles.safe.model", "profiles.\"literal]name\".model"], found.Select(item => item.KeyPath));

        (string candidate, IReadOnlyList<ConfigMutation> mutations) = SecondaryOverridePatcher.Apply(text, new Dictionary<string, string> { ["profiles.safe.model"] = "new" });
        Assert.Equal(text.Replace("\"one\"", "\"new\"", StringComparison.Ordinal), candidate);
        Assert.Single(mutations);
    }

    [Fact]
    public async Task ArrayTableOverridesAreVisibleButCannotBeSelectedForImplicitBatchEditing()
    {
        using var temporary = new TemporaryDirectory();
        string path = Path.Combine(temporary.Path, "config.toml");
        const string text = "[[profiles.workers]]\nmodel = \"one\"\n[[profiles.workers]]\nmodel = \"two\"\n";
        await File.WriteAllTextAsync(path, text);
        SecondaryModelOverride item = Assert.Single(await new SecondaryModelOverrideScanner(new TomlConfigPatchEngine()).ScanAsync(path));
        Assert.False(item.CanEdit);
        Assert.Null(item.RawTomlValue);
        Assert.Contains("无法", item.Detail, StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => SecondaryOverridePatcher.Apply(text, new Dictionary<string, string> { [item.KeyPath] = "new" }));
        Assert.Equal(text, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public void SecondaryMultilineOriginalCanBeRestoredWithoutChangingComments()
    {
        const string original = "review_model = '''old\nline''' # keep\n";
        (string changed, _) = SecondaryOverridePatcher.Apply(original, new Dictionary<string, string> { ["review_model"] = "new" });
        (string restored, _) = SecondaryOverridePatcher.Apply(changed, new Dictionary<string, SecondaryOverrideReplacement>
        {
            ["review_model"] = new("old\nline", "'''old\nline'''"),
        });
        Assert.Equal(original, restored);
        Assert.Throws<InvalidDataException>(() => SecondaryOverridePatcher.Apply(changed, new Dictionary<string, SecondaryOverrideReplacement>
        {
            ["review_model"] = new("different", "'old'"),
        }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SecondarySelectionAndRestoreKeepCaseDistinctTomlKeysSeparate(bool external)
    {
        const string profileText = "[profiles.A]\nmodel = 'gpt-upper'\n[profiles.a]\nmodel = 'gpt-lower'\n";
        using var harness = new SwitchHarness("model = \"gpt-native\"\n" + (external ? string.Empty : profileText));
        string path = Path.Combine(harness.Home.Home, external ? "worker.config.toml" : "config.toml");
        if (external)
        {
            await File.WriteAllTextAsync(path, profileText);
        }
        string Select(string key) => JsonSerializer.Serialize(new[] { new SecondaryOverrideTarget(path.ToUpperInvariant(), key) });
        SwitchRequest followUpper = harness.Request(ProviderKind.LmStudio) with
        {
            SecondaryOverridePolicy = SecondaryOverridePolicy.FollowMain,
            SecondaryOverrideSelectionJson = Select("profiles.A.model"),
        };
        await harness.Service.CommitAsync(await harness.Service.CreatePlanAsync(followUpper));
        Assert.Contains("[profiles.A]\nmodel = \"qwen/local@q6\"", await File.ReadAllTextAsync(path), StringComparison.Ordinal);
        Assert.Contains("[profiles.a]\nmodel = 'gpt-lower'", await File.ReadAllTextAsync(path), StringComparison.Ordinal);
        Assert.Single((await harness.Settings.LoadAsync()).SecondaryOverrideOriginals);

        SwitchRequest followLower = followUpper with { SecondaryOverrideSelectionJson = Select("profiles.a.model") };
        await harness.Service.CommitAsync(await harness.Service.CreatePlanAsync(followLower));
        AppSettings saved = await harness.Settings.LoadAsync();
        Assert.Equal(2, saved.SecondaryOverrideOriginals.Count);
        Assert.True(saved.SecondaryOverrideOriginals.ContainsKey(path.ToLowerInvariant() + "|profiles.A.model"));
        Assert.True(saved.SecondaryOverrideOriginals.ContainsKey(path.ToLowerInvariant() + "|profiles.a.model"));

        SwitchRequest restoreUpper = harness.Request(ProviderKind.OpenAI) with
        {
            SecondaryOverridePolicy = SecondaryOverridePolicy.RestoreOriginal,
            SecondaryOverrideSelectionJson = Select("profiles.A.model"),
        };
        await harness.Service.CommitAsync(await harness.Service.CreatePlanAsync(restoreUpper));
        string restoredUpper = await File.ReadAllTextAsync(path);
        Assert.Contains("[profiles.A]\nmodel = 'gpt-upper'", restoredUpper, StringComparison.Ordinal);
        Assert.Contains("[profiles.a]\nmodel = \"qwen/local@q6\"", restoredUpper, StringComparison.Ordinal);
        Assert.Single((await harness.Settings.LoadAsync()).SecondaryOverrideOriginals);
        await harness.Service.CommitAsync(await harness.Service.CreatePlanAsync(restoreUpper with { SecondaryOverrideSelectionJson = Select("profiles.a.model") }));
        Assert.EndsWith(profileText, await File.ReadAllTextAsync(path), StringComparison.Ordinal);
        Assert.Empty((await harness.Settings.LoadAsync()).SecondaryOverrideOriginals);
    }

    [Theory]
    [InlineData("experimental_bearer_token = \"fixture-secret-duplicate\"\nexperimental_bearer_token = \"again\"\n")]
    [InlineData("experimental_bearer_token = \"fixture-secret-duplicate\" trailing\n")]
    public void TomlDiagnosticDoesNotExposeOriginalSourceOrInnerExceptions(string text)
    {
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => new TomlConfigPatchEngine().Validate(text));
        Assert.Contains("语法或语义无效", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-secret-duplicate", error.ToString(), StringComparison.Ordinal);
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StageFailureOrCancellationCleansEveryRegisteredCandidate(bool cancel)
    {
        using var temporary = new TemporaryDirectory();
        using var cancellation = new CancellationTokenSource();
        string first = Path.Combine(temporary.Path, "first.toml");
        string second = Path.Combine(temporary.Path, "second.toml");
        await File.WriteAllTextAsync(first, "model = \"first-old\"\n");
        await File.WriteAllTextAsync(second, "model = \"second-old\"\n");
        PlannedFileChange[] changes =
        [
            new(first, await FileFingerprintService.CaptureAsync(first), Encoding.UTF8.GetBytes("model = \"first-new\"\n"), []),
            new(second, await FileFingerprintService.CaptureAsync(second), Encoding.UTF8.GetBytes("model = \"fixture-secret\"\n"), [], _ =>
            {
                if (!cancel)
                {
                    throw new InvalidDataException("injected stage failure");
                }
                cancellation.Cancel();
                return ValueTask.CompletedTask;
            }),
        ];
        Exception? error = await Record.ExceptionAsync(() => new AtomicBatchWriter().WriteAsync(changes, cancellation.Token));
        if (cancel)
        {
            Assert.IsAssignableFrom<OperationCanceledException>(error);
        }
        else
        {
            Assert.IsType<InvalidDataException>(error);
        }
        Assert.Empty(Directory.EnumerateFiles(temporary.Path, "*.tmp"));
        Assert.Empty(Directory.EnumerateFiles(temporary.Path, "*.rollback"));
        Assert.Equal("model = \"first-old\"\n", await File.ReadAllTextAsync(first));
        Assert.Equal("model = \"second-old\"\n", await File.ReadAllTextAsync(second));
    }

    [Fact]
    public async Task FailedDeletionOfNewCandidateIsReportedAsRollbackFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        using var temporary = new TemporaryDirectory();
        string path = Path.Combine(temporary.Path, "new.toml");
        var primary = new InvalidDataException("injected post-commit failure");
        int validations = 0;
        var change = new PlannedFileChange(path, FileFingerprint.Missing, Encoding.UTF8.GetBytes("model = \"new\"\n"), [], _ =>
        {
            validations++;
            if (validations == 2)
            {
                File.SetAttributes(path, FileAttributes.ReadOnly);
                throw primary;
            }
            return ValueTask.CompletedTask;
        });
        try
        {
            AggregateException error = await Assert.ThrowsAsync<AggregateException>(() => new AtomicBatchWriter().WriteAsync([change]));
            Assert.Same(primary, error.InnerExceptions[0]);
            Assert.IsType<AggregateException>(error.InnerExceptions[1]);
            Assert.True(File.Exists(path));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }
        }
    }

    [Theory]
    [InlineData("{\"files\":null}")]
    [InlineData("{\"files\":[null]}")]
    [InlineData("{}")]
    [InlineData("null")]
    public async Task InvalidManifestShapeIsOneInvalidHistoryItemAndRestoreHasNoSideEffects(string invalidManifest)
    {
        using var temporary = new TemporaryDirectory();
        var home = new TestCodexHomeProvider(Path.Combine(temporary.Path, "home"));
        string config = Path.Combine(home.Home, "config.toml");
        await File.WriteAllTextAsync(config, "model = \"safe\"\n");
        var backups = new BackupService(home, new AtomicBatchWriter(), new TomlConfigPatchEngine());
        string valid = await backups.CreateHistorySnapshotAsync(BackupOperation.Manual, null, null, null, null);
        string invalid = Path.Combine(backups.BackupRoot, "history", "invalid");
        Directory.CreateDirectory(invalid);
        await File.WriteAllTextAsync(Path.Combine(invalid, "manifest.json"), invalidManifest);

        IReadOnlyList<BackupSnapshotInfo> history = await backups.ListHistoryAsync();
        Assert.Equal(2, history.Count);
        Assert.True(history.Single(item => item.Directory == valid).HashesValid);
        Assert.False(history.Single(item => item.Directory == invalid).HashesValid);
        await Assert.ThrowsAsync<InvalidDataException>(() => backups.RestoreAsync(invalid));
        Assert.Equal(2, Directory.GetDirectories(Path.Combine(backups.BackupRoot, "history")).Length);
        Assert.Equal("model = \"safe\"\n", await File.ReadAllTextAsync(config));
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("duplicate")]
    [InlineData("hash")]
    [InlineData("relative-original")]
    [InlineData("missing-primary")]
    public async Task MalformedNonEmptyManifestFailsBeforeRestore(string corruption)
    {
        using var temporary = new TemporaryDirectory();
        var home = new TestCodexHomeProvider(Path.Combine(temporary.Path, "home"));
        await File.WriteAllTextAsync(Path.Combine(home.Home, "config.toml"), "model = \"safe\"\n");
        var backups = new BackupService(home, new AtomicBatchWriter(), new TomlConfigPatchEngine());
        string snapshot = await backups.CreateHistorySnapshotAsync(BackupOperation.Manual, null, null, null, null);
        string manifestPath = Path.Combine(snapshot, "manifest.json");
        JsonObject manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
        JsonArray files = manifest["files"]!.AsArray();
        switch (corruption)
        {
            case "schema": manifest["schemaVersion"] = 99; break;
            case "duplicate": files.Add(files[0]!.DeepClone()); break;
            case "hash": files[0]!["sha256"] = "invalid"; break;
            case "relative-original": files[0]!["originalPath"] = "relative.toml"; break;
            case "missing-primary": files.RemoveAt(1); break;
        }
        await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());
        Assert.False(Assert.Single(await backups.ListHistoryAsync()).HashesValid);
        await Assert.ThrowsAsync<InvalidDataException>(() => backups.RestoreAsync(snapshot));
        Assert.Single(Directory.GetDirectories(Path.Combine(backups.BackupRoot, "history")));
    }
}
