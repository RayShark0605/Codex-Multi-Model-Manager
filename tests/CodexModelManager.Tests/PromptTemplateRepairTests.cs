using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexModelManager.Core.LmStudio;
using CodexModelManager.Core.Models;

namespace CodexModelManager.Tests;

public sealed class PromptTemplateRepairTests
{
    [Theory]
    [InlineData(2u)]
    [InlineData(3u)]
    public async Task ReaderExtractsOnlyRequiredGgufMetadata(uint version)
    {
        using var temporary = new TemporaryDirectory();
        string path = Path.Combine(temporary.Path, $"fixture-v{version}.gguf");
        WriteGguf(path, version,
        [
            ("general.name", 8u, "Qwen Fixture"),
            ("general.architecture", 8u, "qwen-fixture"),
            ("unrelated.scalar", 4u, null),
            ("tokenizer.chat_template", 8u, SupportedTemplate),
        ]);
        var reader = new GgufChatTemplateReader();

        GgufChatTemplateAnalysis analysis = await reader.ReadAsync(path);

        Assert.Equal(version, analysis.GgufVersion);
        Assert.Equal("Qwen Fixture", analysis.ModelName);
        Assert.Equal("qwen-fixture", analysis.Architecture);
        Assert.Equal(SupportedTemplate, analysis.ChatTemplate);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(SupportedTemplate))), analysis.TemplateSha256);
    }

    [Fact]
    public async Task ReaderRejectsNonGgufTruncationUnknownTypeAndDuplicateTemplate()
    {
        using var temporary = new TemporaryDirectory();
        var reader = new GgufChatTemplateReader();
        string nonGguf = Path.Combine(temporary.Path, "not.gguf");
        await File.WriteAllTextAsync(nonGguf, "not a model");
        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadAsync(nonGguf));

        string truncated = Path.Combine(temporary.Path, "truncated.gguf");
        await File.WriteAllBytesAsync(truncated, "GGUF\u0003"u8.ToArray());
        await Assert.ThrowsAsync<EndOfStreamException>(() => reader.ReadAsync(truncated));

        string unknown = Path.Combine(temporary.Path, "unknown.gguf");
        WriteGguf(unknown, 3, [("unknown", 99u, null)]);
        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadAsync(unknown));

        string duplicate = Path.Combine(temporary.Path, "duplicate.gguf");
        WriteGguf(duplicate, 3,
        [
            ("tokenizer.chat_template", 8u, SupportedTemplate),
            ("tokenizer.chat_template", 8u, SupportedTemplate),
        ]);
        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadAsync(duplicate));
    }

    [Fact]
    public void SupportedQwenTemplateGetsMinimalInstructionHierarchyPatch()
    {
        var service = new PromptTemplateRepairService();
        GgufChatTemplateAnalysis analysis = Analysis(SupportedTemplate);

        PromptTemplateRepairPreview preview = service.CreatePreview(analysis);

        Assert.True(
            preview.Status == PromptTemplateRepairStatus.Supported,
            $"Template SHA {analysis.TemplateSha256} was {preview.Status}: {preview.Detail}");
        Assert.NotNull(preview.PatchedTemplate);
        Assert.Contains("CMM-CODEX-INSTRUCTION-HIERARCHY qwen-interleaved-instructions-v3", preview.PatchedTemplate, StringComparison.Ordinal);
        Assert.Contains("instruction.role == 'system' or instruction.role == 'developer'", preview.PatchedTemplate, StringComparison.Ordinal);
        Assert.Contains("for instruction in messages", preview.PatchedTemplate, StringComparison.Ordinal);
        Assert.DoesNotContain("messages[:cmm_instruction_state.count]", preview.PatchedTemplate, StringComparison.Ordinal);
        Assert.DoesNotContain("System and developer messages must precede conversation messages.", preview.PatchedTemplate, StringComparison.Ordinal);
        Assert.DoesNotContain("System message must be at the beginning.", preview.PatchedTemplate, StringComparison.Ordinal);
        Assert.DoesNotContain("messages[0].role == 'system'", preview.PatchedTemplate, StringComparison.Ordinal);
        Assert.Contains("if message.role == \"system\" or message.role == \"developer\"", preview.PatchedTemplate, StringComparison.Ordinal);
        Assert.Contains("set content = ''", preview.PatchedTemplate, StringComparison.Ordinal);
        int skipIndex = preview.PatchedTemplate!.IndexOf("set content = ''", StringComparison.Ordinal);
        int conversationRenderIndex = preview.PatchedTemplate.IndexOf("render_content(message.content, true)|trim", skipIndex, StringComparison.Ordinal);
        Assert.True(skipIndex >= 0 && conversationRenderIndex > skipIndex);
        Assert.Contains("ASSISTANT_SENTINEL", preview.PatchedTemplate, StringComparison.Ordinal);
        Assert.Contains("TOOL_SENTINEL", preview.PatchedTemplate, StringComparison.Ordinal);
        Assert.Contains("REASONING_SENTINEL", preview.PatchedTemplate, StringComparison.Ordinal);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(preview.PatchedTemplate!))), preview.PatchedTemplateSha256);
    }

    [Fact]
    public void CrLfIsPreservedAndUnknownOrMixedTemplatesAreRejected()
    {
        var service = new PromptTemplateRepairService();
        string lf = SupportedTemplate.Replace("\r\n", "\n", StringComparison.Ordinal);
        string crlf = lf.Replace("\n", "\r\n", StringComparison.Ordinal);
        PromptTemplateRepairPreview crlfPreview = service.CreatePreview(Analysis(crlf));
        Assert.Equal(PromptTemplateRepairStatus.Supported, crlfPreview.Status);
        Assert.DoesNotContain("\n", crlfPreview.PatchedTemplate!.Replace("\r\n", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);

        PromptTemplateRepairPreview unknown = service.CreatePreview(Analysis("{{ messages }}"));
        Assert.Equal(PromptTemplateRepairStatus.Unsupported, unknown.Status);
        Assert.Null(unknown.PatchedTemplate);

        string mixed = crlf + "\nlone";
        PromptTemplateRepairPreview mixedPreview = service.CreatePreview(Analysis(mixed));
        Assert.Equal(PromptTemplateRepairStatus.Unsupported, mixedPreview.Status);
    }

    [Fact]
    public void ReasoningAwareQwenSystemBlockIsPatchedWithoutDroppingReasoningInstructions()
    {
        var service = new PromptTemplateRepairService();
        GgufChatTemplateAnalysis analysis = Analysis(CreateReasoningAwareTemplate());

        PromptTemplateRepairPreview preview = service.CreatePreview(analysis);

        Assert.True(
            preview.Status == PromptTemplateRepairStatus.Supported,
            $"Template was {preview.Status}: {preview.Detail}");
        Assert.Contains("cmm_render_state.content = reasoning_instructions", preview.PatchedTemplate, StringComparison.Ordinal);
        Assert.Contains("cmm_render_state.content + '\\n\\n' + content", preview.PatchedTemplate, StringComparison.Ordinal);
        Assert.DoesNotContain("messages[0].role == 'system'", preview.PatchedTemplate, StringComparison.Ordinal);
        Assert.Contains("ASSISTANT_SENTINEL", preview.PatchedTemplate, StringComparison.Ordinal);
        Assert.Contains("REASONING_SENTINEL", preview.PatchedTemplate, StringComparison.Ordinal);
        Assert.Contains("TOOL_SENTINEL", preview.PatchedTemplate, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyExactManagerPatchIsRecognizedAsAlreadyCompatible()
    {
        var service = new PromptTemplateRepairService();
        PromptTemplateRepairPreview generated = service.CreatePreview(Analysis(SupportedTemplate));

        PromptTemplateRepairPreview known = service.CreatePreview(Analysis(generated.PatchedTemplate!));
        PromptTemplateRepairPreview markerOnly = service.CreatePreview(Analysis(
            "{# CMM-CODEX-INSTRUCTION-HIERARCHY qwen-leading-instructions-v2 #}\n{{ messages }}"));

        Assert.Equal(PromptTemplateRepairStatus.AlreadyCompatible, known.Status);
        Assert.Equal(PromptTemplateRepairStatus.Unsupported, markerOnly.Status);
        Assert.Null(markerOnly.PatchedTemplate);
    }

    [Fact]
    public void ExactV2TemplateIsUpgradeRequiredAndCanBeDeterministicallyRecreated()
    {
        var service = new PromptTemplateRepairService();
        string v2 = PromptTemplateRepairService.PatchExactQwenTemplateV2(SupportedTemplate);
        string v2Sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(v2)));

        PromptTemplateRepairPreview upgraded = service.CreatePreview(Analysis(v2));
        string recreated = service.RecreateKnownTemplate(
            Analysis(SupportedTemplate),
            PromptTemplateRepairService.LegacyLeadingRuleVersion,
            v2Sha);

        Assert.Equal(PromptTemplateRepairStatus.UpgradeRequired, upgraded.Status);
        Assert.Equal(PromptTemplateRepairService.CurrentRuleVersion, upgraded.RuleVersion);
        Assert.Contains("qwen-leading-instructions-v2", v2, StringComparison.Ordinal);
        Assert.Contains("System and developer messages must precede conversation messages.", v2, StringComparison.Ordinal);
        Assert.DoesNotContain("set content = ''", v2, StringComparison.Ordinal);
        Assert.DoesNotContain("System and developer messages must precede conversation messages.", upgraded.PatchedTemplate, StringComparison.Ordinal);
        Assert.Contains("set content = ''", upgraded.PatchedTemplate, StringComparison.Ordinal);
        Assert.Equal(v2, recreated);
        Assert.Throws<InvalidDataException>(() => service.RecreateKnownTemplate(
            Analysis(SupportedTemplate),
            PromptTemplateRepairService.LegacyLeadingRuleVersion,
            new string('0', 64)));
    }

    [Fact]
    public void UnknownManagerMarkerIsNeverGuessed()
    {
        var service = new PromptTemplateRepairService();
        PromptTemplateRepairPreview preview = service.CreatePreview(Analysis(
            SupportedTemplate.Replace(
                "{%- if not messages %}",
                "{# CMM-CODEX-INSTRUCTION-HIERARCHY qwen-unknown-v99 #}\n{%- if not messages %}",
                StringComparison.Ordinal)));

        Assert.Equal(PromptTemplateRepairStatus.Unsupported, preview.Status);
        Assert.Null(preview.PatchedTemplate);
    }

    [Fact]
    public void PrefixMergedSystemTemplateGetsExactInterleavedV3Patch()
    {
        string source = ReadPrefixMergedSystemFixture();
        Assert.Equal(
            "12827F24B742EA4E80CDC12DBCF9622227056B9F797252A3149263D4F9AAADCE",
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))));
        var service = new PromptTemplateRepairService();

        PromptTemplateRepairPreview preview = service.CreatePreview(Analysis(source));

        Assert.Equal(PromptTemplateRepairStatus.Supported, preview.Status);
        Assert.Equal(
            "9DC0DA000D1DF280BE9F6F64D314EB52879C0DF5C3C951F74105964136592F85",
            preview.PatchedTemplateSha256);
        string patched = Assert.IsType<string>(preview.PatchedTemplate);
        Assert.Contains("CMM-CODEX-INSTRUCTION-HIERARCHY qwen-interleaved-instructions-v3", patched, StringComparison.Ordinal);
        Assert.Contains("for instruction in messages", patched, StringComparison.Ordinal);
        Assert.Contains("instruction.role == 'system' or instruction.role == 'developer'", patched, StringComparison.Ordinal);
        Assert.Contains("cmm_instruction_state.text + ('\\n\\n' if cmm_instruction_state.text else '')", patched, StringComparison.Ordinal);
        Assert.Contains("if message.role == \"system\" or message.role == \"developer\"", patched, StringComparison.Ordinal);
        Assert.Contains("set content = ''", patched, StringComparison.Ordinal);
        Assert.DoesNotContain("sysns.count == loop.index0", patched, StringComparison.Ordinal);
        Assert.DoesNotContain("loop.index0 >= num_sys", patched, StringComparison.Ordinal);
        Assert.DoesNotContain("System message must be at the beginning.", patched, StringComparison.Ordinal);
        Assert.Contains("for message in messages[::-1]", patched, StringComparison.Ordinal);
        Assert.Contains("<|vision_start|><|image_pad|><|vision_end|>", patched, StringComparison.Ordinal);
        Assert.Contains("message.reasoning_content is string", patched, StringComparison.Ordinal);
        Assert.Contains("message.tool_calls and message.tool_calls is iterable", patched, StringComparison.Ordinal);
        Assert.Contains("<tool_response>", patched, StringComparison.Ordinal);
        Assert.Contains("if add_generation_prompt", patched, StringComparison.Ordinal);
        Assert.Equal(
            patched,
            service.RecreateKnownTemplate(Analysis(source), PromptTemplateRepairService.CurrentRuleVersion, preview.PatchedTemplateSha256!));
        Assert.Equal(PromptTemplateRepairStatus.AlreadyCompatible, service.CreatePreview(Analysis(patched)).Status);
    }

    [Fact]
    public void PrefixMergedSystemTemplatePreservesCrLfAndRejectsMixedNewLines()
    {
        string source = ReadPrefixMergedSystemFixture();
        string crLf = source.Replace("\n", "\r\n", StringComparison.Ordinal);
        var service = new PromptTemplateRepairService();

        PromptTemplateRepairPreview preview = service.CreatePreview(Analysis(crLf));
        PromptTemplateRepairPreview mixed = service.CreatePreview(Analysis(crLf + "\nlone"));

        Assert.Equal(PromptTemplateRepairStatus.Supported, preview.Status);
        Assert.DoesNotContain("\n", preview.PatchedTemplate!.Replace("\r\n", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Equal(PromptTemplateRepairStatus.Unsupported, mixed.Status);
        Assert.Null(mixed.PatchedTemplate);
    }

    [Fact]
    public void EveryPrefixMergedSystemStructuralNearMatchRemainsUnsupported()
    {
        string source = ReadPrefixMergedSystemFixture();
        (string Anchor, string Replacement)[] mutations =
        [
            ("macro render_content(content, do_vision_count, is_system_content=false)", "macro render_content(content, do_vision_count, is_system_content=true)"),
            ("sysns.count == loop.index0", "sysns.count <= loop.index0"),
            ("set merged_system = sysns.text", "set merged_system = sysns.text|trim"),
            ("if tools and tools is iterable and tools is not mapping", "if tools and tools is iterable"),
            ("for message in messages[::-1]", "for message in messages|reverse"),
            ("loop.index0 >= num_sys", "loop.index0 > num_sys"),
            ("System message must be at the beginning.", "System message must remain first."),
            ("elif message.role == \"user\"", "elif message.role == \"human\""),
            ("elif message.role == \"assistant\"", "elif message.role == \"model\""),
            ("elif message.role == \"tool\"", "elif message.role == \"function\""),
            ("<|vision_start|><|image_pad|><|vision_end|>", "<|vision_start|><|image|><|vision_end|>"),
            ("message.reasoning_content is string", "message.reasoning_content is defined"),
            ("message.tool_calls and message.tool_calls is iterable", "message.tool_calls and message.tool_calls is sequence"),
            ("if add_generation_prompt", "if add_generation_prompt is true"),
            ("Unsloth fixes - developer role, merged system messages, tool calling", "changed non-target footer"),
        ];
        var service = new PromptTemplateRepairService();

        foreach ((string anchor, string replacement) in mutations)
        {
            string mutated = source.Replace(anchor, replacement, StringComparison.Ordinal);
            Assert.NotEqual(source, mutated);
            PromptTemplateRepairPreview preview = service.CreatePreview(Analysis(mutated));
            Assert.True(
                preview.Status == PromptTemplateRepairStatus.Unsupported,
                $"Near-match anchor unexpectedly passed: {anchor}");
            Assert.Null(preview.PatchedTemplate);
        }
    }

    [Fact]
    public void GeneratedPrefixMergedSystemV3RejectsNonTargetBranchMutation()
    {
        var service = new PromptTemplateRepairService();
        PromptTemplateRepairPreview generated = service.CreatePreview(Analysis(ReadPrefixMergedSystemFixture()));
        string mutated = generated.PatchedTemplate!.Replace(
            "<tool_response>",
            "<changed_tool_response>",
            StringComparison.Ordinal);

        PromptTemplateRepairPreview preview = service.CreatePreview(Analysis(mutated));

        Assert.Equal(PromptTemplateRepairStatus.Unsupported, preview.Status);
        Assert.Null(preview.PatchedTemplate);
    }
    [Fact]
    public async Task ExportWritesAuditableArtifactsWithoutAbsoluteSourcePathOrSecrets()
    {
        using var temporary = new TemporaryDirectory();
        var service = new PromptTemplateRepairService();
        string sourcePath = Path.Combine(temporary.Path, "private", "secret-model.gguf");
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        WriteGguf(sourcePath, 3, [
            ("general.name", 8u, "Fixture"),
            ("general.architecture", 8u, "qwen-fixture"),
            ("tokenizer.chat_template", 8u, SupportedTemplate),
        ]);
        GgufChatTemplateAnalysis analysis = await new GgufChatTemplateReader().ReadAsync(sourcePath);

        PromptTemplateRepairArtifact artifact = await service.ExportAsync(analysis, "qwen/local@q6", Path.Combine(temporary.Path, "exports"));

        Assert.True(File.Exists(artifact.OriginalTemplatePath));
        Assert.True(File.Exists(artifact.PatchedTemplatePath));
        Assert.True(File.Exists(artifact.ManifestPath));
        Assert.True(File.Exists(artifact.ApplyInstructionsPath));
        Assert.Equal(SupportedTemplate, await File.ReadAllTextAsync(artifact.OriginalTemplatePath));
        string manifest = await File.ReadAllTextAsync(artifact.ManifestPath);
        using JsonDocument document = JsonDocument.Parse(manifest);
        Assert.Equal(analysis.TemplateSha256, document.RootElement.GetProperty("originalTemplateSha256").GetString());
        Assert.Equal(artifact.PatchedTemplateSha256, document.RootElement.GetProperty("patchedTemplateSha256").GetString());
        Assert.DoesNotContain(temporary.Path, manifest, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Authorization", manifest, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Bearer", manifest, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExportRejectsAnalysisWhoseTemplateHashWasTampered()
    {
        using var temporary = new TemporaryDirectory();
        var service = new PromptTemplateRepairService();
        string sourcePath = Path.Combine(temporary.Path, "model.gguf");
        WriteGguf(sourcePath, 3, [("tokenizer.chat_template", 8u, SupportedTemplate)]);
        GgufChatTemplateAnalysis analysis = (await new GgufChatTemplateReader().ReadAsync(sourcePath)) with
        {
            TemplateSha256 = new string('0', 64),
        };
        string output = Path.Combine(temporary.Path, "exports");

        await Assert.ThrowsAsync<InvalidDataException>(() => service.ExportAsync(analysis, "qwen/local@q6", output));

        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task ExportRejectsGgufChangedAfterAnalysis()
    {
        using var temporary = new TemporaryDirectory();
        string sourcePath = Path.Combine(temporary.Path, "model.gguf");
        WriteGguf(sourcePath, 3, [("tokenizer.chat_template", 8u, SupportedTemplate)]);
        var reader = new GgufChatTemplateReader();
        GgufChatTemplateAnalysis analysis = await reader.ReadAsync(sourcePath);
        string changedTemplate = SupportedTemplate.Replace("ASSISTANT_SENTINEL", "ASSISTANT_CHANGED", StringComparison.Ordinal);
        WriteGguf(sourcePath, 3, [("tokenizer.chat_template", 8u, changedTemplate)]);
        File.SetLastWriteTimeUtc(sourcePath, analysis.LastWriteTimeUtc.UtcDateTime.AddSeconds(2));
        var service = new PromptTemplateRepairService(reader);
        string output = Path.Combine(temporary.Path, "exports");

        await Assert.ThrowsAsync<IOException>(() => service.ExportAsync(analysis, "qwen/local@q6", output));

        Assert.False(Directory.Exists(output));
    }

    [Fact]
    [Trait("Category", "LiveGguf")]
    public async Task CurrentGgufTemplateCanBeReadAndConservativelyAnalyzed()
    {
        string? path = Environment.GetEnvironmentVariable("CMM_LIVE_GGUF_PATH");
        Assert.SkipUnless(!string.IsNullOrWhiteSpace(path), "Set CMM_LIVE_GGUF_PATH to run the live read-only GGUF template test.");
        var reader = new GgufChatTemplateReader();
        var service = new PromptTemplateRepairService();

        GgufChatTemplateAnalysis analysis = await reader.ReadAsync(path!, TestContext.Current.CancellationToken);
        PromptTemplateRepairPreview preview = service.CreatePreview(analysis);

        string? expectedSha = Environment.GetEnvironmentVariable("CMM_LIVE_GGUF_TEMPLATE_SHA");
        if (!string.IsNullOrWhiteSpace(expectedSha)) Assert.Equal(expectedSha, analysis.TemplateSha256, ignoreCase: true);
        Assert.True(
            preview.Status == PromptTemplateRepairStatus.Supported,
            $"Template SHA {analysis.TemplateSha256} was {preview.Status}: {preview.Detail}");
        Assert.NotNull(preview.PatchedTemplate);
        using var temporary = new TemporaryDirectory();
        PromptTemplateRepairArtifact artifact = await service.ExportAsync(analysis, "live-read-only-model", temporary.Path, TestContext.Current.CancellationToken);
        Assert.True(File.Exists(artifact.PatchedTemplatePath));
        Assert.Equal(preview.PatchedTemplateSha256, artifact.PatchedTemplateSha256);
        string manifest = await File.ReadAllTextAsync(artifact.ManifestPath, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(Path.GetDirectoryName(path!)!, manifest, StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadPrefixMergedSystemFixture() => File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "unsloth-qwen3.8-prefix-template.jinja"));
    private static GgufChatTemplateAnalysis Analysis(string template) => new(
        "C:\\fixture\\model.gguf",
        "model.gguf",
        1024,
        DateTimeOffset.UnixEpoch,
        3,
        "Fixture",
        "qwen-fixture",
        template,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(template))));

    private static void WriteGguf(string path, uint version, IReadOnlyList<(string Key, uint Type, string? Value)> metadata)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false);
        writer.Write("GGUF"u8);
        writer.Write(version);
        writer.Write(0ul);
        writer.Write(checked((ulong)metadata.Count));
        foreach ((string key, uint type, string? value) in metadata)
        {
            WriteString(writer, key);
            writer.Write(type);
            switch (type)
            {
                case 4:
                    writer.Write(123u);
                    break;
                case 8:
                    WriteString(writer, value ?? string.Empty);
                    break;
            }
        }
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(checked((ulong)bytes.Length));
        writer.Write(bytes);
    }

    private static string CreateReasoningAwareTemplate()
    {
        const string simple = """
                {%- if messages[0].role == 'system' %}
                    {%- set content = render_content(messages[0].content, false, true)|trim %}
                    {{- '<|im_start|>system\n' + content + '<|im_end|>\n' }}
                {%- endif %}
            """;
        const string reasoning = """
                {%- if messages[0].role == 'system' %}
                    {%- set content = render_content(messages[0].content, false, true)|trim %}
                    {%- if content %}
                        {{- '<|im_start|>system\n' + (reasoning_instructions + '\n\n' if reasoning_instructions else '')  + content + '<|im_end|>\n' }}
                    {%- elif reasoning_instructions %}
                        {{- '<|im_start|>system\n' + reasoning_instructions + '<|im_end|>\n' }}
                    {%- endif %}
                {%- elif reasoning_instructions %}
                    {{- '<|im_start|>system\n' + reasoning_instructions + '<|im_end|>\n' }}
                {%- endif %}
            """;
        string result = SupportedTemplate.Replace(simple, reasoning, StringComparison.Ordinal);
        Assert.NotEqual(SupportedTemplate, result);
        return result;
    }

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
}
