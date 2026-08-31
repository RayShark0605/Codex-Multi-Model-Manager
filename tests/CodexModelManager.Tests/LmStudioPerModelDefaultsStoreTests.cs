using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexModelManager.Core.Infrastructure;
using CodexModelManager.Core.LmStudio;
using CodexModelManager.Core.Models;

namespace CodexModelManager.Tests;

public sealed class LmStudioPerModelDefaultsStoreTests
{
    [Theory]
    [InlineData("0.4.21")]
    [InlineData("0.4.21.0")]
    [InlineData("0.4.21+2")]
    [InlineData("0.4.23")]
    [InlineData("0.4.23.0")]
    [InlineData("0.4.23+1")]
    [InlineData("0.4.23.0+1")]
    public async Task VerifiedVersionFamiliesCreateReadOnlyPlans(string version)
    {
        using var fixture = CreateFixture();
        byte[] original = await File.ReadAllBytesAsync(fixture.DefaultsPath);

        LmStudioPerModelDefaultsPlan plan = await fixture.CreatePlanAsync(version: version);

        Assert.Equal(version, plan.LmStudioVersion);
        Assert.Equal(LmStudioPerModelDefaultsMutation.Add, plan.Mutation);
        Assert.Equal(original, await File.ReadAllBytesAsync(fixture.DefaultsPath));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-version")]
    [InlineData("0.4.23-alpha")]
    [InlineData("0.4.23+build")]
    [InlineData("0.4.23+1+2")]
    [InlineData("0.4.20.0")]
    [InlineData("0.4.22.0")]
    [InlineData("0.4.24.0")]
    [InlineData("0.5.0")]
    public async Task UnverifiedVersionsAreRejectedBeforeAnyDefaultsMutation(string? version)
    {
        using var fixture = CreateFixture();
        byte[] original = await File.ReadAllBytesAsync(fixture.DefaultsPath);

        NotSupportedException exception = await Assert.ThrowsAsync<NotSupportedException>(() => fixture.CreatePlanAsync(version: version));

        Assert.Contains("0.4.21.x / 0.4.23.x", exception.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllBytesAsync(fixture.DefaultsPath));
    }

    [Fact]
    public async Task MissingPromptFieldCreatesExactV3CandidateAndPreservesAllOtherSemantics()
    {
        using var fixture = CreateFixture();
        JsonObject before = JsonNode.Parse(await File.ReadAllTextAsync(fixture.DefaultsPath))!.AsObject();

        LmStudioPerModelDefaultsPlan plan = await fixture.CreatePlanAsync();

        Assert.Equal(LmStudioPerModelDefaultsMutation.Add, plan.Mutation);
        Assert.Equal(LmStudioPersistentTemplateFieldState.Missing, plan.OriginalFieldState);
        Assert.Equal(fixture.ConcreteIdentifier, plan.ConcreteModelIdentifier);
        Assert.Equal(fixture.DefaultsPath, plan.FilePath);
        JsonObject after = JsonNode.Parse(plan.CandidateBytes)!.AsObject();
        RemovePrompt(after);
        Assert.True(JsonNode.DeepEquals(before, after));
        Assert.Contains(PromptTemplateRepairService.CurrentRuleVersion, Encoding.UTF8.GetString(plan.CandidateBytes), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExactV3IsNoOpAndExactV2WithCompletedProvenanceIsUpgraded()
    {
        using var fixture = CreateFixture();
        LmStudioPerModelDefaultsPlan add = await fixture.CreatePlanAsync();
        await File.WriteAllBytesAsync(fixture.DefaultsPath, add.CandidateBytes);

        LmStudioPerModelDefaultsPlan noOp = await fixture.CreatePlanAsync();

        Assert.Equal(LmStudioPerModelDefaultsMutation.NoOp, noOp.Mutation);
        Assert.Equal(LmStudioPersistentTemplateFieldState.ManagerV3, noOp.OriginalFieldState);
        Assert.Equal(noOp.OriginalFingerprint.Sha256, noOp.CandidateFingerprint.Sha256);

        string v2 = PromptTemplateRepairService.PatchExactQwenTemplateV2(fixture.Analysis.ChatTemplate);
        string v2Sha = Sha(v2);
        WriteDefaults(fixture.DefaultsPath, v2);
        var provenance = new LmStudioRuntimeTemplateProvenance(
            LmStudioRuntimeTemplateMode.ManagerRule,
            PromptTemplateRepairService.LegacyLeadingRuleVersion,
            v2Sha,
            Guid.NewGuid());

        LmStudioPerModelDefaultsPlan upgrade = await fixture.CreatePlanAsync(provenance);

        Assert.Equal(LmStudioPerModelDefaultsMutation.Upgrade, upgrade.Mutation);
        Assert.Equal(LmStudioPersistentTemplateFieldState.ManagerV2, upgrade.OriginalFieldState);
        Assert.Equal(v2Sha, upgrade.OriginalTemplateSha256);
        Assert.Equal(fixture.Preview.PatchedTemplateSha256, upgrade.TargetTemplateSha256);
    }

    [Fact]
    public async Task V2WithoutCompletedProvenanceAndUnknownCustomTemplateAreBlocked()
    {
        using var fixture = CreateFixture();
        string v2 = PromptTemplateRepairService.PatchExactQwenTemplateV2(fixture.Analysis.ChatTemplate);
        WriteDefaults(fixture.DefaultsPath, v2);

        InvalidDataException missingProvenance = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.CreatePlanAsync());
        Assert.Contains("不会覆盖", missingProvenance.Message, StringComparison.Ordinal);

        WriteDefaults(fixture.DefaultsPath, "custom-template");
        InvalidDataException custom = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.CreatePlanAsync());
        Assert.Contains("用户自定义", custom.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("custom-template", custom.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DuplicateOrInvalidPromptShapeAndInvalidJsonAreBlocked()
    {
        using var fixture = CreateFixture();
        string target = fixture.Preview.PatchedTemplate!;
        JsonObject duplicate = CreateDefaults(target);
        GetFields(duplicate).Add(CreatePromptField(target));
        await File.WriteAllTextAsync(fixture.DefaultsPath, duplicate.ToJsonString());
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.CreatePlanAsync());

        JsonObject invalidShape = CreateDefaults();
        GetFields(invalidShape).Add(new JsonObject { ["key"] = LmStudioPerModelDefaultsStore.PromptTemplateKey, ["value"] = "wrong" });
        await File.WriteAllTextAsync(fixture.DefaultsPath, invalidShape.ToJsonString());
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.CreatePlanAsync());

        await File.WriteAllTextAsync(fixture.DefaultsPath, "{");
        await Assert.ThrowsAnyAsync<JsonException>(() => fixture.CreatePlanAsync());
    }

    [Fact]
    public async Task OversizedAndExcessivelyDeepFilesAreBlocked()
    {
        using var fixture = CreateFixture();
        await File.WriteAllBytesAsync(fixture.DefaultsPath, new byte[(2 * 1024 * 1024) + 1]);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.CreatePlanAsync());

        var text = new StringBuilder("{\"preset\":\"\",\"operation\":{\"fields\":[]},\"load\":{\"fields\":[]},\"deep\":");
        for (int index = 0; index < 40; index++) text.Append("{\"x\":");
        text.Append('0');
        for (int index = 0; index < 40; index++) text.Append('}');
        text.Append('}');
        await File.WriteAllTextAsync(fixture.DefaultsPath, text.ToString());
        await Assert.ThrowsAnyAsync<JsonException>(() => fixture.CreatePlanAsync());
    }

    [Theory]
    [InlineData("../model.gguf")]
    [InlineData("publisher/../model.gguf")]
    [InlineData("C:/models/model.gguf")]
    [InlineData("//server/share/model.gguf")]
    [InlineData("publisher/model.bin")]
    [InlineData("publisher//model.gguf")]
    public void UnsafeConcreteIdentifiersAreRejected(string identifier)
    {
        using var temporary = new TemporaryDirectory();
        var store = new LmStudioPerModelDefaultsStore(new PromptTemplateRepairService(), new AtomicBatchWriter(), new PassthroughProtector(), temporary.Path);

        Assert.ThrowsAny<Exception>(() => store.GetDefaultsPath(identifier));
    }

    [Fact]
    public void ExistingReparsePointAncestorIsRejected()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temporary = new TemporaryDirectory();
        string target = Path.Combine(temporary.Path, "actual-defaults");
        string junction = Path.Combine(temporary.Path, "defaults-link");
        Directory.CreateDirectory(target);
        Directory.CreateSymbolicLink(junction, target);
        var store = new LmStudioPerModelDefaultsStore(new PromptTemplateRepairService(), new AtomicBatchWriter(), new PassthroughProtector(), junction);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => store.GetDefaultsPath("publisher/model.gguf"));

        Assert.Contains("reparse point", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RemoteEndpointUnknownVersionAndNonProcessIdentityAreRejectedBeforeWrite()
    {
        using var fixture = CreateFixture();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.CreatePlanAsync(endpoint: new Uri("https://example.invalid:1234")));
        await Assert.ThrowsAsync<NotSupportedException>(() => fixture.CreatePlanAsync(version: "0.4.22.0"));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.CreatePlanAsync(resolution: fixture.Resolution with { Source = "manual" }));
    }

    [Fact]
    public async Task BackupApplyAndExactRestoreRoundTripOriginalBytes()
    {
        using var fixture = CreateFixture();
        byte[] original = await File.ReadAllBytesAsync(fixture.DefaultsPath);
        LmStudioPerModelDefaultsPlan plan = await fixture.CreatePlanAsync();
        string backupPath = Path.Combine(fixture.Temporary.Path, "backup", "defaults.dpapi");

        LmStudioDefaultsBackupArtifact backup = await fixture.Store.CreateVerifiedBackupAsync(plan, backupPath);
        await fixture.Store.ApplyAsync(plan);
        FileFingerprint applied = await LmStudioPerModelDefaultsStore.VerifyAppliedAsync(plan);
        LmStudioDefaultsRestoreResult restored = await fixture.Store.RestoreAsync(plan, backup);

        Assert.Equal(plan.CandidateFingerprint.Sha256, applied.Sha256);
        Assert.True(restored.Succeeded, restored.Detail);
        Assert.False(restored.RecoveryBlocked);
        Assert.Equal(original, await File.ReadAllBytesAsync(fixture.DefaultsPath));
    }

    [Fact]
    public async Task RestorePreservesUnrelatedConcurrentChangesButBlocksExternalPromptReplacement()
    {
        using var fixture = CreateFixture();
        LmStudioPerModelDefaultsPlan plan = await fixture.CreatePlanAsync();
        LmStudioDefaultsBackupArtifact backup = await fixture.Store.CreateVerifiedBackupAsync(plan, Path.Combine(fixture.Temporary.Path, "backup.dpapi"));
        await fixture.Store.ApplyAsync(plan);
        JsonObject concurrent = JsonNode.Parse(await File.ReadAllTextAsync(fixture.DefaultsPath))!.AsObject();
        concurrent["external"] = 42;
        await File.WriteAllTextAsync(fixture.DefaultsPath, concurrent.ToJsonString());

        LmStudioDefaultsRestoreResult surgical = await fixture.Store.RestoreAsync(plan, backup);

        Assert.True(surgical.Succeeded, surgical.Detail);
        JsonObject after = JsonNode.Parse(await File.ReadAllTextAsync(fixture.DefaultsPath))!.AsObject();
        Assert.Equal(42, after["external"]!.GetValue<int>());
        Assert.DoesNotContain(GetFields(after), node => node!["key"]!.GetValue<string>() == LmStudioPerModelDefaultsStore.PromptTemplateKey);

        await File.WriteAllBytesAsync(fixture.DefaultsPath, plan.CandidateBytes);
        JsonObject changedPrompt = JsonNode.Parse(await File.ReadAllTextAsync(fixture.DefaultsPath))!.AsObject();
        JsonObject field = GetFields(changedPrompt).Select(node => node!.AsObject()).Single(node => node["key"]!.GetValue<string>() == LmStudioPerModelDefaultsStore.PromptTemplateKey);
        field["value"]!["jinjaPromptTemplate"]!["template"] = "external-custom";
        await File.WriteAllTextAsync(fixture.DefaultsPath, changedPrompt.ToJsonString());

        LmStudioDefaultsRestoreResult blocked = await fixture.Store.RestoreAsync(plan, backup);

        Assert.False(blocked.Succeeded);
        Assert.True(blocked.RecoveryBlocked);
        Assert.Contains("外部修改", blocked.Detail, StringComparison.Ordinal);
        Assert.Contains("external-custom", await File.ReadAllTextAsync(fixture.DefaultsPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FingerprintDriftAndCorruptBackupFailClosed()
    {
        using var fixture = CreateFixture();
        LmStudioPerModelDefaultsPlan plan = await fixture.CreatePlanAsync();
        await File.AppendAllTextAsync(fixture.DefaultsPath, " ");
        await Assert.ThrowsAsync<IOException>(() => fixture.Store.CreateVerifiedBackupAsync(plan, Path.Combine(fixture.Temporary.Path, "drift.dpapi")));

        await File.WriteAllBytesAsync(fixture.DefaultsPath, plan.OriginalBytes);
        plan = await fixture.CreatePlanAsync();
        LmStudioDefaultsBackupArtifact backup = await fixture.Store.CreateVerifiedBackupAsync(plan, Path.Combine(fixture.Temporary.Path, "valid.dpapi"));
        await fixture.Store.ApplyAsync(plan);
        await File.AppendAllTextAsync(backup.Path, "corrupt");

        LmStudioDefaultsRestoreResult result = await fixture.Store.RestoreAsync(plan, backup);

        Assert.False(result.Succeeded);
        Assert.True(result.RecoveryBlocked);
        Assert.Equal(plan.CandidateFingerprint.Sha256, (await FileFingerprintService.CaptureAsync(fixture.DefaultsPath)).Sha256);
    }

    [Fact]
    public void WindowsProtectorUsesCurrentUserDpapiRoundTrip()
    {
        if (!OperatingSystem.IsWindows()) return;
        var protector = new WindowsCurrentUserDpapiProtector();
        byte[] plaintext = "per-model-defaults"u8.ToArray();

        byte[] encrypted = protector.Protect(plaintext);
        byte[] decrypted = protector.Unprotect(encrypted);

        Assert.NotEqual(plaintext, encrypted);
        Assert.Equal(plaintext, decrypted);
    }

    private static StoreFixture CreateFixture()
    {
        var temporary = new TemporaryDirectory();
        const string concrete = "unsloth/Qwen3.8-Flash-Next-GGUF/Qwen3.8-Flash-Next-UD-IQ4_XS-00001-of-00003.gguf";
        string gguf = Path.Combine(temporary.Path, "models", "Qwen3.8-Flash-Next-UD-IQ4_XS-00001-of-00003.gguf");
        Directory.CreateDirectory(Path.GetDirectoryName(gguf)!);
        File.WriteAllBytes(gguf, "dummy"u8.ToArray());
        const string template = SupportedTemplate;
        var analysis = new GgufChatTemplateAnalysis(gguf, Path.GetFileName(gguf), new FileInfo(gguf).Length, File.GetLastWriteTimeUtc(gguf), 3, "Qwen", "qwen35", template, Sha(template));
        var repair = new PromptTemplateRepairService();
        PromptTemplateRepairPreview preview = repair.CreatePreview(analysis);
        Assert.Equal(PromptTemplateRepairStatus.Supported, preview.Status);
        string root = Path.Combine(temporary.Path, "defaults-root");
        var store = new LmStudioPerModelDefaultsStore(repair, new AtomicBatchWriter(), new PassthroughProtector(), root);
        string defaults = store.GetDefaultsPath(concrete);
        Directory.CreateDirectory(Path.GetDirectoryName(defaults)!);
        WriteDefaults(defaults);
        var resolution = new LmStudioModelFileResolution(gguf, concrete.ToLowerInvariant(), null, "qwen35", null, "lms ps --json", concrete);
        return new StoreFixture(temporary, store, concrete, defaults, resolution, analysis, preview);
    }

    private static void WriteDefaults(string path, string? promptTemplate = null) =>
        File.WriteAllText(path, CreateDefaults(promptTemplate).ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

    private static JsonObject CreateDefaults(string? promptTemplate = null)
    {
        var root = new JsonObject
        {
            ["preset"] = string.Empty,
            ["operation"] = new JsonObject { ["fields"] = new JsonArray() },
            ["load"] = new JsonObject
            {
                ["fields"] = new JsonArray
                {
                    new JsonObject { ["key"] = "llm.load.contextLength", ["value"] = 196_608 },
                    new JsonObject { ["key"] = "llm.load.llama.acceleration.offloadRatio", ["value"] = 1 },
                    new JsonObject { ["key"] = "llm.load.llama.cpuThreadPoolSize", ["value"] = 16 },
                    new JsonObject { ["key"] = "llm.load.numParallelSessions", ["value"] = 1 },
                    new JsonObject { ["key"] = "llm.load.llama.contextCheckpoints", ["value"] = 16 },
                    new JsonObject { ["key"] = "llm.load.numCpuExpertLayersRatio", ["value"] = 0.7083333333333334 },
                    new JsonObject { ["key"] = "llm.load.llama.kCacheQuantizationType", ["value"] = new JsonObject { ["checked"] = true, ["value"] = "q8_0" } },
                    new JsonObject { ["key"] = "llm.load.llama.vCacheQuantizationType", ["value"] = new JsonObject { ["checked"] = true, ["value"] = "q8_0" } },
                    new JsonObject { ["key"] = "llm.load.llama.evalBatchSize", ["value"] = 2048 },
                    new JsonObject { ["key"] = "unknown.future.setting", ["value"] = new JsonObject { ["nested"] = true } },
                },
            },
            ["unknownRoot"] = "preserve-me",
        };
        if (promptTemplate is not null) GetFields(root).Add(CreatePromptField(promptTemplate));
        return root;
    }

    private static JsonObject CreatePromptField(string template) => new()
    {
        ["key"] = LmStudioPerModelDefaultsStore.PromptTemplateKey,
        ["value"] = new JsonObject
        {
            ["type"] = "jinja",
            ["jinjaPromptTemplate"] = new JsonObject { ["template"] = template },
        },
    };

    private static JsonArray GetFields(JsonObject root) => (JsonArray)root["load"]!["fields"]!;

    private static void RemovePrompt(JsonObject root)
    {
        JsonArray fields = GetFields(root);
        for (int index = fields.Count - 1; index >= 0; index--)
        {
            if (fields[index]!["key"]!.GetValue<string>() == LmStudioPerModelDefaultsStore.PromptTemplateKey) fields.RemoveAt(index);
        }
    }

    private static string Sha(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private const string SupportedTemplate = """
        {%- macro render_content(content, do_vision_count, is_system_content=false) %}
            {{- content }}
        {%- endmacro %}
        {%- if not messages %}
            {{- raise_exception('No messages provided.') }}
        {%- endif %}
        {%- if tools %}
            {{- '<|im_start|>system\nTOOLS' }}
            {%- if messages[0].role == 'system' %}
                {%- set content = render_content(messages[0].content, false, true)|trim %}
                {%- if content %}
                    {{- '\n\n' + content }}
                {%- endif %}
            {%- endif %}
            {{- '<|im_end|>\n' }}
        {%- else %}
            {%- if messages[0].role == 'system' %}
                {%- set content = render_content(messages[0].content, false, true)|trim %}
                {{- '<|im_start|>system\n' + content + '<|im_end|>\n' }}
            {%- endif %}
        {%- endif %}
        {%- for message in messages %}
            {%- set content = render_content(message.content, true)|trim %}
            {%- if message.role == "system" %}
                {%- if not loop.first %}
                    {{- raise_exception('System message must be at the beginning.') }}
                {%- endif %}
            {%- elif message.role == "user" %}
                {{- content }}
            {%- elif message.role == "assistant" %}
                {{- 'ASSISTANT_SENTINEL' }}
                {{- 'REASONING_SENTINEL' }}
            {%- elif message.role == "tool" %}
                {{- 'TOOL_SENTINEL' }}
            {%- endif %}
        {%- endfor %}
        """;

    private sealed record StoreFixture(
        TemporaryDirectory Temporary,
        LmStudioPerModelDefaultsStore Store,
        string ConcreteIdentifier,
        string DefaultsPath,
        LmStudioModelFileResolution Resolution,
        GgufChatTemplateAnalysis Analysis,
        PromptTemplateRepairPreview Preview) : IDisposable
    {
        public Task<LmStudioPerModelDefaultsPlan> CreatePlanAsync(
            LmStudioRuntimeTemplateProvenance? provenance = null,
            Uri? endpoint = null,
            string? version = "0.4.21.0",
            LmStudioModelFileResolution? resolution = null) =>
            Store.CreatePlanAsync(
                endpoint ?? new Uri("http://127.0.0.1:1234"),
                version,
                resolution ?? Resolution,
                Analysis,
                Preview,
                provenance ?? new LmStudioRuntimeTemplateProvenance(LmStudioRuntimeTemplateMode.BuiltIn));

        public void Dispose() => Temporary.Dispose();
    }

    private sealed class PassthroughProtector : ILmStudioDefaultsProtector
    {
        public byte[] Protect(byte[] plaintext) => [0x43, 0x4D, 0x4D, .. plaintext];

        public byte[] Unprotect(byte[] ciphertext)
        {
            if (ciphertext.Length < 3 || ciphertext[0] != 0x43 || ciphertext[1] != 0x4D || ciphertext[2] != 0x4D)
            {
                throw new CryptographicException("invalid fixture envelope");
            }

            return ciphertext[3..];
        }
    }
}
