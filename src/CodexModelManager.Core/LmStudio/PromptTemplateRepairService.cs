using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexModelManager.Core.Abstractions;
using CodexModelManager.Core.Models;

namespace CodexModelManager.Core.LmStudio;

public sealed class PromptTemplateRepairService : IPromptTemplateRepairService
{
    public const string RuleVersion = "qwen-leading-instructions-v2";
    private const string Marker = "{# CMM-CODEX-INSTRUCTION-HIERARCHY qwen-leading-instructions-v2 #}";
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static readonly JsonSerializerOptions ManifestJsonOptions = new() { WriteIndented = true };
    private readonly IGgufChatTemplateReader ggufReader;

    public PromptTemplateRepairService(IGgufChatTemplateReader? ggufReader = null)
    {
        this.ggufReader = ggufReader ?? new GgufChatTemplateReader();
    }

    public PromptTemplateRepairPreview CreatePreview(GgufChatTemplateAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        if (analysis.ChatTemplate.Contains(Marker, StringComparison.Ordinal))
        {
            try
            {
                ValidateKnownPatchedTemplate(analysis.ChatTemplate);
                return new PromptTemplateRepairPreview(
                    PromptTemplateRepairStatus.AlreadyCompatible,
                    "模板精确匹配本管理器的 Codex 指令层级修补结构；仍须重载模型并执行实时差分检测。",
                    analysis.ChatTemplate,
                    analysis.TemplateSha256,
                    RuleVersion);
            }
            catch (InvalidDataException exception)
            {
                return new PromptTemplateRepairPreview(
                    PromptTemplateRepairStatus.Unsupported,
                    exception.Message,
                    null,
                    null,
                    RuleVersion);
            }
        }

        try
        {
            string patched = PatchExactQwenTemplate(analysis.ChatTemplate);
            string patchedSha = Convert.ToHexString(SHA256.HashData(Utf8NoBom.GetBytes(patched)));
            return new PromptTemplateRepairPreview(
                PromptTemplateRepairStatus.Supported,
                "模板结构与受支持的 Qwen system-order 模式精确匹配；可以导出最小兼容修补。",
                patched,
                patchedSha,
                RuleVersion);
        }
        catch (InvalidDataException exception)
        {
            return new PromptTemplateRepairPreview(
                PromptTemplateRepairStatus.Unsupported,
                exception.Message,
                null,
                null,
                RuleVersion);
        }
    }

    public async Task<PromptTemplateRepairArtifact> ExportAsync(
        GgufChatTemplateAnalysis analysis,
        string modelId,
        string outputRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);
        cancellationToken.ThrowIfCancellationRequested();
        string computedOriginalSha = Convert.ToHexString(SHA256.HashData(Utf8NoBom.GetBytes(analysis.ChatTemplate)));
        if (!computedOriginalSha.Equals(analysis.TemplateSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("GGUF analysis 中的原模板 SHA-256 与模板正文不一致；请重新分析。");
        }

        GgufChatTemplateAnalysis current = await ggufReader.ReadAsync(analysis.FilePath, cancellationToken).ConfigureAwait(false);
        if (current.FileLength != analysis.FileLength ||
            current.LastWriteTimeUtc != analysis.LastWriteTimeUtc ||
            current.GgufVersion != analysis.GgufVersion ||
            !current.TemplateSha256.Equals(analysis.TemplateSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("GGUF 或其 Prompt Template 在分析后发生变化；请重新分析，未导出旧模板。");
        }

        analysis = current;

        PromptTemplateRepairPreview preview = CreatePreview(analysis);
        if (preview.Status != PromptTemplateRepairStatus.Supported || preview.PatchedTemplate is null || preview.PatchedTemplateSha256 is null)
        {
            throw new InvalidOperationException("只有结构精确匹配且尚未修补的模板才能导出兼容修补：" + preview.Detail);
        }

        string root = Path.GetFullPath(outputRoot);
        Directory.CreateDirectory(root);
        string modelDirectory = Path.Combine(root, SanitizePathSegment(modelId));
        Directory.CreateDirectory(modelDirectory);
        string directory = Path.Combine(modelDirectory, DateTime.Now.ToString("yyyyMMdd-HHmmssfff", System.Globalization.CultureInfo.InvariantCulture));
        if (Directory.Exists(directory))
        {
            directory += "-" + Guid.NewGuid().ToString("N")[..8];
        }

        Directory.CreateDirectory(directory);
        try
        {
            string originalPath = Path.Combine(directory, "original-chat-template.jinja");
            string patchedPath = Path.Combine(directory, "codex-compatible-chat-template.jinja");
            string manifestPath = Path.Combine(directory, "manifest.json");
            string applyPath = Path.Combine(directory, "APPLY.md");
            await WriteNewAsync(originalPath, Utf8NoBom.GetBytes(analysis.ChatTemplate), cancellationToken).ConfigureAwait(false);
            await WriteNewAsync(patchedPath, Utf8NoBom.GetBytes(preview.PatchedTemplate), cancellationToken).ConfigureAwait(false);

            var manifest = new
            {
                schemaVersion = 1,
                createdAt = DateTimeOffset.Now,
                appVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown",
                ruleVersion = RuleVersion,
                modelId,
                gguf = new
                {
                    fileName = analysis.FileName,
                    version = analysis.GgufVersion,
                    length = analysis.FileLength,
                    modelName = analysis.ModelName,
                    architecture = analysis.Architecture,
                },
                originalTemplateSha256 = analysis.TemplateSha256,
                patchedTemplateSha256 = preview.PatchedTemplateSha256,
            };
            byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, ManifestJsonOptions);
            await WriteNewAsync(manifestPath, manifestBytes, cancellationToken).ConfigureAwait(false);

            string apply = BuildApplyInstructions(modelId, analysis.TemplateSha256, preview.PatchedTemplateSha256);
            await WriteNewAsync(applyPath, Utf8NoBom.GetBytes(apply), cancellationToken).ConfigureAwait(false);
            return new PromptTemplateRepairArtifact(
                directory,
                originalPath,
                patchedPath,
                manifestPath,
                applyPath,
                analysis.TemplateSha256,
                preview.PatchedTemplateSha256);
        }
        catch
        {
            string fullDirectory = Path.GetFullPath(directory);
            if (fullDirectory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Directory.Exists(fullDirectory))
            {
                Directory.Delete(fullDirectory, recursive: true);
            }

            throw;
        }
    }

    internal static string PatchExactQwenTemplate(string template)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(template);
        string newLine = DetectNewLine(template);
        string normalized = newLine == "\n" ? template : template.Replace("\r\n", "\n", StringComparison.Ordinal);

        RequireCount(normalized, "{%- macro render_content(content, do_vision_count, is_system_content=false) %}", 1, "render_content 宏");
        RequireCount(normalized, "{%- for message in messages %}", 1, "主 messages 循环");
        RequireCount(normalized, "System message must be at the beginning.", 1, "system-order 异常");

        const string noMessagesBlock = """
            {%- if not messages %}
                {{- raise_exception('No messages provided.') }}
            {%- endif %}
            """;
        const string toolsSystemBlock = """
                {%- if messages[0].role == 'system' %}
                    {%- set content = render_content(messages[0].content, false, true)|trim %}
                    {%- if content %}
                        {{- '\n\n' + content }}
                    {%- endif %}
                {%- endif %}
            """;
        const string plainSimpleSystemBlock = """
                {%- if messages[0].role == 'system' %}
                    {%- set content = render_content(messages[0].content, false, true)|trim %}
                    {{- '<|im_start|>system\n' + content + '<|im_end|>\n' }}
                {%- endif %}
            """;
        const string plainReasoningSystemBlock = """
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
        const string loopSystemBlock = """
                {%- if message.role == "system" %}
                    {%- if not loop.first %}
                        {{- raise_exception('System message must be at the beginning.') }}
                    {%- endif %}
            """;

        string noMessagesPattern = NormalizeRawLiteral(noMessagesBlock);
        string toolsSystemPattern = NormalizeRawLiteral(toolsSystemBlock);
        string plainSimpleSystemPattern = NormalizeRawLiteral(plainSimpleSystemBlock);
        string plainReasoningSystemPattern = NormalizeRawLiteral(plainReasoningSystemBlock);
        string loopSystemPattern = NormalizeRawLiteral(loopSystemBlock);
        RequireCount(normalized, noMessagesPattern, 1, "空消息检查区");
        RequireCount(normalized, toolsSystemPattern, 1, "tools system 区");
        int simpleSystemCount = CountOccurrences(normalized, plainSimpleSystemPattern);
        int reasoningSystemCount = CountOccurrences(normalized, plainReasoningSystemPattern);
        if (simpleSystemCount + reasoningSystemCount != 1)
        {
            throw new InvalidDataException($"Unsupported Template：普通 system 区必须精确匹配一个受支持变体；simple={simpleSystemCount}, reasoning={reasoningSystemCount}。未生成猜测模板。");
        }

        RequireCount(normalized, loopSystemPattern, 1, "messages system 分支");

        const string instructionScan = """
            {%- if not messages %}
                {{- raise_exception('No messages provided.') }}
            {%- endif %}
            {# CMM-CODEX-INSTRUCTION-HIERARCHY qwen-leading-instructions-v2 #}
            {%- set cmm_instruction_state = namespace(seen_conversation=false, count=0) %}
            {%- for message in messages %}
                {%- if message.role == 'system' or message.role == 'developer' %}
                    {%- if cmm_instruction_state.seen_conversation %}
                        {{- raise_exception('System and developer messages must precede conversation messages.') }}
                    {%- endif %}
                    {%- set cmm_instruction_state.count = cmm_instruction_state.count + 1 %}
                {%- else %}
                    {%- set cmm_instruction_state.seen_conversation = true %}
                {%- endif %}
            {%- endfor %}
            """;
        const string patchedToolsSystem = """
                {%- for instruction in messages[:cmm_instruction_state.count] %}
                    {%- set content = render_content(instruction.content, false, true)|trim %}
                    {%- if content %}
                        {{- '\n\n' + content }}
                    {%- endif %}
                {%- endfor %}
            """;
        const string patchedPlainSimpleSystem = """
                {%- set cmm_render_state = namespace(content='') %}
                {%- for instruction in messages[:cmm_instruction_state.count] %}
                    {%- set content = render_content(instruction.content, false, true)|trim %}
                    {%- if content %}
                        {%- if cmm_render_state.content %}
                            {%- set cmm_render_state.content = cmm_render_state.content + '\n\n' + content %}
                        {%- else %}
                            {%- set cmm_render_state.content = content %}
                        {%- endif %}
                    {%- endif %}
                {%- endfor %}
                {%- if cmm_render_state.content %}
                    {{- '<|im_start|>system\n' + cmm_render_state.content + '<|im_end|>\n' }}
                {%- endif %}
            """;
        const string patchedPlainReasoningSystem = """
                {%- set cmm_render_state = namespace(content='') %}
                {%- if reasoning_instructions %}
                    {%- set cmm_render_state.content = reasoning_instructions %}
                {%- endif %}
                {%- for instruction in messages[:cmm_instruction_state.count] %}
                    {%- set content = render_content(instruction.content, false, true)|trim %}
                    {%- if content %}
                        {%- if cmm_render_state.content %}
                            {%- set cmm_render_state.content = cmm_render_state.content + '\n\n' + content %}
                        {%- else %}
                            {%- set cmm_render_state.content = content %}
                        {%- endif %}
                    {%- endif %}
                {%- endfor %}
                {%- if cmm_render_state.content %}
                    {{- '<|im_start|>system\n' + cmm_render_state.content + '<|im_end|>\n' }}
                {%- endif %}
            """;
        const string patchedLoopSystem = """
                {%- if message.role == "system" or message.role == "developer" %}
                    {%- if loop.index0 >= cmm_instruction_state.count %}
                        {{- raise_exception('System and developer messages must precede conversation messages.') }}
                    {%- endif %}
            """;

        string patchedPlainSystem = simpleSystemCount == 1
            ? NormalizeRawLiteral(patchedPlainSimpleSystem)
            : NormalizeRawLiteral(patchedPlainReasoningSystem);
        string sourcePlainSystem = simpleSystemCount == 1 ? plainSimpleSystemPattern : plainReasoningSystemPattern;
        string patched = normalized
            .Replace(noMessagesPattern, NormalizeRawLiteral(instructionScan), StringComparison.Ordinal)
            .Replace(toolsSystemPattern, NormalizeRawLiteral(patchedToolsSystem), StringComparison.Ordinal)
            .Replace(sourcePlainSystem, patchedPlainSystem, StringComparison.Ordinal)
            .Replace(loopSystemPattern, NormalizeRawLiteral(patchedLoopSystem), StringComparison.Ordinal);

        if (patched.Contains("System message must be at the beginning.", StringComparison.Ordinal) ||
            CountOccurrences(patched, Marker) != 1)
        {
            throw new InvalidDataException("模板修补后的结构校验失败，已拒绝导出。");
        }

        return newLine == "\n" ? patched : patched.Replace("\n", "\r\n", StringComparison.Ordinal);
    }

    private static void ValidateKnownPatchedTemplate(string template)
    {
        string newLine = DetectNewLine(template);
        string normalized = newLine == "\n" ? template : template.Replace("\r\n", "\n", StringComparison.Ordinal);
        RequireCount(normalized, "{%- macro render_content(content, do_vision_count, is_system_content=false) %}", 1, "render_content 宏");
        RequireCount(normalized, "{%- for message in messages %}", 2, "instruction 扫描与主 messages 循环");
        RequireCount(normalized, Marker, 1, "管理器兼容标记");
        RequireCount(normalized, "System message must be at the beginning.", 0, "旧 system-order 异常");

        const string instructionScan = """
            {%- if not messages %}
                {{- raise_exception('No messages provided.') }}
            {%- endif %}
            {# CMM-CODEX-INSTRUCTION-HIERARCHY qwen-leading-instructions-v2 #}
            {%- set cmm_instruction_state = namespace(seen_conversation=false, count=0) %}
            {%- for message in messages %}
                {%- if message.role == 'system' or message.role == 'developer' %}
                    {%- if cmm_instruction_state.seen_conversation %}
                        {{- raise_exception('System and developer messages must precede conversation messages.') }}
                    {%- endif %}
                    {%- set cmm_instruction_state.count = cmm_instruction_state.count + 1 %}
                {%- else %}
                    {%- set cmm_instruction_state.seen_conversation = true %}
                {%- endif %}
            {%- endfor %}
            """;
        const string patchedToolsSystem = """
                {%- for instruction in messages[:cmm_instruction_state.count] %}
                    {%- set content = render_content(instruction.content, false, true)|trim %}
                    {%- if content %}
                        {{- '\n\n' + content }}
                    {%- endif %}
                {%- endfor %}
            """;
        const string patchedPlainSimpleSystem = """
                {%- set cmm_render_state = namespace(content='') %}
                {%- for instruction in messages[:cmm_instruction_state.count] %}
                    {%- set content = render_content(instruction.content, false, true)|trim %}
                    {%- if content %}
                        {%- if cmm_render_state.content %}
                            {%- set cmm_render_state.content = cmm_render_state.content + '\n\n' + content %}
                        {%- else %}
                            {%- set cmm_render_state.content = content %}
                        {%- endif %}
                    {%- endif %}
                {%- endfor %}
                {%- if cmm_render_state.content %}
                    {{- '<|im_start|>system\n' + cmm_render_state.content + '<|im_end|>\n' }}
                {%- endif %}
            """;
        const string patchedPlainReasoningSystem = """
                {%- set cmm_render_state = namespace(content='') %}
                {%- if reasoning_instructions %}
                    {%- set cmm_render_state.content = reasoning_instructions %}
                {%- endif %}
                {%- for instruction in messages[:cmm_instruction_state.count] %}
                    {%- set content = render_content(instruction.content, false, true)|trim %}
                    {%- if content %}
                        {%- if cmm_render_state.content %}
                            {%- set cmm_render_state.content = cmm_render_state.content + '\n\n' + content %}
                        {%- else %}
                            {%- set cmm_render_state.content = content %}
                        {%- endif %}
                    {%- endif %}
                {%- endfor %}
                {%- if cmm_render_state.content %}
                    {{- '<|im_start|>system\n' + cmm_render_state.content + '<|im_end|>\n' }}
                {%- endif %}
            """;
        const string patchedLoopSystem = """
                {%- if message.role == "system" or message.role == "developer" %}
                    {%- if loop.index0 >= cmm_instruction_state.count %}
                        {{- raise_exception('System and developer messages must precede conversation messages.') }}
                    {%- endif %}
            """;

        RequireCount(normalized, NormalizeRawLiteral(instructionScan), 1, "instruction 扫描区");
        RequireCount(normalized, NormalizeRawLiteral(patchedToolsSystem), 1, "tools instruction 合并区");
        int simpleSystemCount = CountOccurrences(normalized, NormalizeRawLiteral(patchedPlainSimpleSystem));
        int reasoningSystemCount = CountOccurrences(normalized, NormalizeRawLiteral(patchedPlainReasoningSystem));
        if (simpleSystemCount + reasoningSystemCount != 1)
        {
            throw new InvalidDataException($"Unsupported Template：已修补的普通 instruction 合并区不匹配；simple={simpleSystemCount}, reasoning={reasoningSystemCount}。");
        }

        RequireCount(normalized, NormalizeRawLiteral(patchedLoopSystem), 1, "后置 instruction 拒绝分支");
    }

    private static string DetectNewLine(string template)
    {
        bool hasCrLf = template.Contains("\r\n", StringComparison.Ordinal);
        bool hasLoneLf = template.Replace("\r\n", string.Empty, StringComparison.Ordinal).Contains('\n');
        if (hasCrLf && hasLoneLf)
        {
            throw new InvalidDataException("Prompt Template 使用混合换行；为保持原文，拒绝自动修补。");
        }

        return hasCrLf ? "\r\n" : "\n";
    }

    private static string NormalizeRawLiteral(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static void RequireCount(string text, string value, int expected, string description)
    {
        int actual = CountOccurrences(text, value);
        if (actual != expected)
        {
            throw new InvalidDataException($"Unsupported Template：{description} 应匹配 {expected} 次，实际 {actual} 次；未生成猜测模板。");
        }
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static string SanitizePathSegment(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string safe = new(value.Select(character => invalid.Contains(character) || character is '/' or '\\' ? '_' : character).ToArray());
        safe = safe.Trim(' ', '.');
        if (safe.Length > 80) safe = safe[..80];
        return string.IsNullOrWhiteSpace(safe) ? "model" : safe;
    }

    private static async Task WriteNewAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static string BuildApplyInstructions(string modelId, string originalSha, string patchedSha) => $$"""
        # Apply the Codex-compatible LM Studio Prompt Template

        Model / loaded instance: `{{modelId}}`

        Original template SHA-256: `{{originalSha}}`

        Patched template SHA-256: `{{patchedSha}}`

        1. Open LM Studio and go to **My Models**.
        2. Open the target model's settings and select **Prompt Template**.
        3. Enable the per-model prompt-template override.
        4. Paste the complete contents of `codex-compatible-chat-template.jinja` and save.
        5. Manually unload and reload that model. The manager never performs this step automatically.
        6. Return to Codex Multi-Model Manager and click **重新检测 Codex 指令层级**.
        7. Do not switch Codex until both the control and Codex-shaped probes pass.

        To undo the change, disable/remove the per-model Prompt Template override in LM Studio and reload the model. The original GGUF is never modified.
        """;
}
