using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexModelManager.Core.Abstractions;
using CodexModelManager.Core.Models;

namespace CodexModelManager.Core.LmStudio;

public sealed class PromptTemplateRepairService : IPromptTemplateRepairService
{
    public const string CurrentRuleVersion = "qwen-interleaved-instructions-v3";
    public const string LegacyLeadingRuleVersion = "qwen-leading-instructions-v2";
    public const string RuleVersion = CurrentRuleVersion;

    private const string V3Marker = "{# CMM-CODEX-INSTRUCTION-HIERARCHY qwen-interleaved-instructions-v3 #}";
    private const string V2Marker = "{# CMM-CODEX-INSTRUCTION-HIERARCHY qwen-leading-instructions-v2 #}";
    private const string GenericMarker = "CMM-CODEX-INSTRUCTION-HIERARCHY";

    private const string SourceNoMessages = """
        {%- if not messages %}
            {{- raise_exception('No messages provided.') }}
        {%- endif %}
        """;

    private const string SourceToolsSystem = """
            {%- if messages[0].role == 'system' %}
                {%- set content = render_content(messages[0].content, false, true)|trim %}
                {%- if content %}
                    {{- '\n\n' + content }}
                {%- endif %}
            {%- endif %}
        """;

    private const string SourcePlainSimpleSystem = """
            {%- if messages[0].role == 'system' %}
                {%- set content = render_content(messages[0].content, false, true)|trim %}
                {{- '<|im_start|>system\n' + content + '<|im_end|>\n' }}
            {%- endif %}
        """;

    private const string SourcePlainReasoningSystem = """
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

    private const string SourceLoopSystem = """
            {%- if message.role == "system" %}
                {%- if not loop.first %}
                    {{- raise_exception('System message must be at the beginning.') }}
                {%- endif %}
        """;

    private const string SourceLoopContent = """
            {%- set content = render_content(message.content, true)|trim %}
        """;

    private const string V2InstructionScan = """
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

    private const string V2ToolsSystem = """
            {%- for instruction in messages[:cmm_instruction_state.count] %}
                {%- set content = render_content(instruction.content, false, true)|trim %}
                {%- if content %}
                    {{- '\n\n' + content }}
                {%- endif %}
            {%- endfor %}
        """;

    private const string V2PlainSimpleSystem = """
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

    private const string V2PlainReasoningSystem = """
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

    private const string V2LoopSystem = """
            {%- if message.role == "system" or message.role == "developer" %}
                {%- if loop.index0 >= cmm_instruction_state.count %}
                    {{- raise_exception('System and developer messages must precede conversation messages.') }}
                {%- endif %}
        """;

    private const string V3NoMessages = """
        {%- if not messages %}
            {{- raise_exception('No messages provided.') }}
        {%- endif %}
        {# CMM-CODEX-INSTRUCTION-HIERARCHY qwen-interleaved-instructions-v3 #}
        """;

    private const string V3ToolsSystem = """
            {%- for instruction in messages %}
                {%- if instruction.role == 'system' or instruction.role == 'developer' %}
                    {%- set content = render_content(instruction.content, false, true)|trim %}
                    {%- if content %}
                        {{- '\n\n' + content }}
                    {%- endif %}
                {%- endif %}
            {%- endfor %}
        """;

    private const string V3PlainSimpleSystem = """
            {%- set cmm_render_state = namespace(content='') %}
            {%- for instruction in messages %}
                {%- if instruction.role == 'system' or instruction.role == 'developer' %}
                    {%- set content = render_content(instruction.content, false, true)|trim %}
                    {%- if content %}
                        {%- if cmm_render_state.content %}
                            {%- set cmm_render_state.content = cmm_render_state.content + '\n\n' + content %}
                        {%- else %}
                            {%- set cmm_render_state.content = content %}
                        {%- endif %}
                    {%- endif %}
                {%- endif %}
            {%- endfor %}
            {%- if cmm_render_state.content %}
                {{- '<|im_start|>system\n' + cmm_render_state.content + '<|im_end|>\n' }}
            {%- endif %}
        """;

    private const string V3PlainReasoningSystem = """
            {%- set cmm_render_state = namespace(content='') %}
            {%- if reasoning_instructions %}
                {%- set cmm_render_state.content = reasoning_instructions %}
            {%- endif %}
            {%- for instruction in messages %}
                {%- if instruction.role == 'system' or instruction.role == 'developer' %}
                    {%- set content = render_content(instruction.content, false, true)|trim %}
                    {%- if content %}
                        {%- if cmm_render_state.content %}
                            {%- set cmm_render_state.content = cmm_render_state.content + '\n\n' + content %}
                        {%- else %}
                            {%- set cmm_render_state.content = content %}
                        {%- endif %}
                    {%- endif %}
                {%- endif %}
            {%- endfor %}
            {%- if cmm_render_state.content %}
                {{- '<|im_start|>system\n' + cmm_render_state.content + '<|im_end|>\n' }}
            {%- endif %}
        """;

    private const string V3LoopSystem = """
            {%- if message.role == "system" or message.role == "developer" %}
                {{- '' }}
        """;

    private const string V3LoopContent = """
            {%- if message.role == "system" or message.role == "developer" %}
                {%- set content = '' %}
            {%- else %}
                {%- set content = render_content(message.content, true)|trim %}
            {%- endif %}
        """;

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
        try
        {
            if (analysis.ChatTemplate.Contains(V3Marker, StringComparison.Ordinal))
            {
                ValidateKnownV3Template(analysis.ChatTemplate);
                return new PromptTemplateRepairPreview(
                    PromptTemplateRepairStatus.AlreadyCompatible,
                    "模板精确匹配 interleaved-instructions v3；仍须重载模型并执行四阶段实时差分检测。",
                    analysis.ChatTemplate,
                    analysis.TemplateSha256,
                    CurrentRuleVersion);
            }

            if (analysis.ChatTemplate.Contains(V2Marker, StringComparison.Ordinal))
            {
                string upgraded = UpgradeExactV2Template(analysis.ChatTemplate);
                return Preview(
                    PromptTemplateRepairStatus.UpgradeRequired,
                    "模板精确匹配旧版 leading-instructions v2；需要升级以支持多轮对话中的后置 developer 指令。",
                    upgraded,
                    CurrentRuleVersion);
            }

            if (analysis.ChatTemplate.Contains(GenericMarker, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Unsupported Template：检测到未知管理器模板规则标记；拒绝猜测升级路径。");
            }

            string patched = PatchExactQwenTemplate(analysis.ChatTemplate);
            return Preview(
                PromptTemplateRepairStatus.Supported,
                "模板结构与受支持的 Qwen system-order 模式精确匹配；可以生成 interleaved-instructions v3 修补。",
                patched,
                CurrentRuleVersion);
        }
        catch (InvalidDataException exception)
        {
            return new PromptTemplateRepairPreview(
                PromptTemplateRepairStatus.Unsupported,
                exception.Message,
                null,
                null,
                CurrentRuleVersion);
        }
    }

    public string RecreateKnownTemplate(
        GgufChatTemplateAnalysis analysis,
        string ruleVersion,
        string expectedTemplateSha256)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedTemplateSha256);
        ValidateAnalysisHash(analysis);

        string recreated = ruleVersion switch
        {
            LegacyLeadingRuleVersion => RecreateV2(analysis.ChatTemplate),
            CurrentRuleVersion => RecreateV3(analysis.ChatTemplate),
            _ => throw new InvalidDataException($"不支持重建 Prompt Template 规则 {ruleVersion}。"),
        };
        string actualSha = ComputeSha(recreated);
        if (!actualSha.Equals(expectedTemplateSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"重建的 {ruleVersion} 模板 SHA-256 与事务证据不一致。");
        }

        return recreated;
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
        ValidateAnalysisHash(analysis);

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
        if (preview.Status is not (PromptTemplateRepairStatus.Supported or PromptTemplateRepairStatus.UpgradeRequired) ||
            preview.PatchedTemplate is null ||
            preview.PatchedTemplateSha256 is null)
        {
            throw new InvalidOperationException("只有结构精确匹配且需要修补或升级的模板才能导出兼容修补：" + preview.Detail);
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
                schemaVersion = 2,
                createdAt = DateTimeOffset.Now,
                appVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown",
                ruleVersion = preview.RuleVersion,
                sourceStatus = preview.Status.ToString(),
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

    internal static string PatchExactQwenTemplate(string template) => PatchOriginalTemplate(template, CurrentRuleVersion);

    public static string PatchExactQwenTemplateV2(string template) => PatchOriginalTemplate(template, LegacyLeadingRuleVersion);

    internal static string UpgradeExactV2Template(string template)
    {
        ValidateKnownV2Template(template);
        string newLine = DetectNewLine(template);
        string normalized = Normalize(template, newLine);
        int simpleCount = CountOccurrences(normalized, NormalizeRawLiteral(V2PlainSimpleSystem));
        string sourcePlain = simpleCount == 1 ? NormalizeRawLiteral(V2PlainSimpleSystem) : NormalizeRawLiteral(V2PlainReasoningSystem);
        string targetPlain = simpleCount == 1 ? NormalizeRawLiteral(V3PlainSimpleSystem) : NormalizeRawLiteral(V3PlainReasoningSystem);
        string upgraded = normalized
            .Replace(NormalizeRawLiteral(V2InstructionScan), NormalizeRawLiteral(V3NoMessages), StringComparison.Ordinal)
            .Replace(NormalizeRawLiteral(V2ToolsSystem), NormalizeRawLiteral(V3ToolsSystem), StringComparison.Ordinal)
            .Replace(sourcePlain, targetPlain, StringComparison.Ordinal)
            .Replace(NormalizeRawLiteral(SourceLoopContent), NormalizeRawLiteral(V3LoopContent), StringComparison.Ordinal)
            .Replace(NormalizeRawLiteral(V2LoopSystem), NormalizeRawLiteral(V3LoopSystem), StringComparison.Ordinal);
        ValidateKnownV3Template(upgraded);
        return RestoreNewLine(upgraded, newLine);
    }

    private static string PatchOriginalTemplate(string template, string ruleVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(template);
        string newLine = DetectNewLine(template);
        string normalized = Normalize(template, newLine);
        ValidateOriginalAnchors(normalized, out bool simpleSystem);

        string targetNoMessages;
        string targetTools;
        string targetPlain;
        string targetLoopContent;
        string targetLoop;
        if (ruleVersion == LegacyLeadingRuleVersion)
        {
            targetNoMessages = NormalizeRawLiteral(V2InstructionScan);
            targetTools = NormalizeRawLiteral(V2ToolsSystem);
            targetPlain = NormalizeRawLiteral(simpleSystem ? V2PlainSimpleSystem : V2PlainReasoningSystem);
            targetLoopContent = NormalizeRawLiteral(SourceLoopContent);
            targetLoop = NormalizeRawLiteral(V2LoopSystem);
        }
        else if (ruleVersion == CurrentRuleVersion)
        {
            targetNoMessages = NormalizeRawLiteral(V3NoMessages);
            targetTools = NormalizeRawLiteral(V3ToolsSystem);
            targetPlain = NormalizeRawLiteral(simpleSystem ? V3PlainSimpleSystem : V3PlainReasoningSystem);
            targetLoopContent = NormalizeRawLiteral(V3LoopContent);
            targetLoop = NormalizeRawLiteral(V3LoopSystem);
        }
        else
        {
            throw new InvalidDataException($"不支持 Prompt Template 规则 {ruleVersion}。");
        }

        string sourcePlain = NormalizeRawLiteral(simpleSystem ? SourcePlainSimpleSystem : SourcePlainReasoningSystem);
        string patched = normalized
            .Replace(NormalizeRawLiteral(SourceNoMessages), targetNoMessages, StringComparison.Ordinal)
            .Replace(NormalizeRawLiteral(SourceToolsSystem), targetTools, StringComparison.Ordinal)
            .Replace(sourcePlain, targetPlain, StringComparison.Ordinal)
            .Replace(NormalizeRawLiteral(SourceLoopContent), targetLoopContent, StringComparison.Ordinal)
            .Replace(NormalizeRawLiteral(SourceLoopSystem), targetLoop, StringComparison.Ordinal);

        if (ruleVersion == LegacyLeadingRuleVersion)
        {
            ValidateKnownV2Template(patched);
        }
        else
        {
            ValidateKnownV3Template(patched);
        }

        return RestoreNewLine(patched, newLine);
    }

    private static void ValidateOriginalAnchors(string normalized, out bool simpleSystem)
    {
        RequireCount(normalized, "{%- macro render_content(content, do_vision_count, is_system_content=false) %}", 1, "render_content 宏");
        RequireCount(normalized, "{%- for message in messages %}", 1, "主 messages 循环");
        RequireCount(normalized, "System message must be at the beginning.", 1, "system-order 异常");
        RequireCount(normalized, NormalizeRawLiteral(SourceNoMessages), 1, "空消息检查区");
        RequireCount(normalized, NormalizeRawLiteral(SourceToolsSystem), 1, "tools system 区");
        RequireCount(normalized, NormalizeRawLiteral(SourceLoopContent), 1, "主循环 content 渲染区");
        int simpleSystemCount = CountOccurrences(normalized, NormalizeRawLiteral(SourcePlainSimpleSystem));
        int reasoningSystemCount = CountOccurrences(normalized, NormalizeRawLiteral(SourcePlainReasoningSystem));
        if (simpleSystemCount + reasoningSystemCount != 1)
        {
            throw new InvalidDataException($"Unsupported Template：普通 system 区必须精确匹配一个受支持变体；simple={simpleSystemCount}, reasoning={reasoningSystemCount}。未生成猜测模板。");
        }

        RequireCount(normalized, NormalizeRawLiteral(SourceLoopSystem), 1, "messages system 分支");
        simpleSystem = simpleSystemCount == 1;
    }

    private static void ValidateKnownV2Template(string template)
    {
        string newLine = DetectNewLine(template);
        string normalized = Normalize(template, newLine);
        RequireCount(normalized, "{%- macro render_content(content, do_vision_count, is_system_content=false) %}", 1, "render_content 宏");
        RequireCount(normalized, "{%- for message in messages %}", 2, "v2 instruction 扫描与主 messages 循环");
        RequireCount(normalized, V2Marker, 1, "v2 管理器兼容标记");
        RequireCount(normalized, V3Marker, 0, "v3 管理器兼容标记");
        RequireCount(normalized, "System message must be at the beginning.", 0, "旧 system-order 异常");
        RequireCount(normalized, NormalizeRawLiteral(V2InstructionScan), 1, "v2 instruction 扫描区");
        RequireCount(normalized, NormalizeRawLiteral(V2ToolsSystem), 1, "v2 tools instruction 合并区");
        RequireCount(normalized, NormalizeRawLiteral(SourceLoopContent), 1, "v2 主循环 content 渲染区");
        RequireCount(normalized, NormalizeRawLiteral(V3LoopContent), 0, "v3 主循环 instruction 跳过区");
        int simpleSystemCount = CountOccurrences(normalized, NormalizeRawLiteral(V2PlainSimpleSystem));
        int reasoningSystemCount = CountOccurrences(normalized, NormalizeRawLiteral(V2PlainReasoningSystem));
        if (simpleSystemCount + reasoningSystemCount != 1)
        {
            throw new InvalidDataException($"Unsupported Template：v2 普通 instruction 合并区不匹配；simple={simpleSystemCount}, reasoning={reasoningSystemCount}。");
        }

        RequireCount(normalized, NormalizeRawLiteral(V2LoopSystem), 1, "v2 后置 instruction 拒绝分支");
    }

    private static void ValidateKnownV3Template(string template)
    {
        string newLine = DetectNewLine(template);
        string normalized = Normalize(template, newLine);
        RequireCount(normalized, "{%- macro render_content(content, do_vision_count, is_system_content=false) %}", 1, "render_content 宏");
        RequireCount(normalized, "{%- for message in messages %}", 1, "v3 主 messages 循环");
        RequireCount(normalized, V3Marker, 1, "v3 管理器兼容标记");
        RequireCount(normalized, V2Marker, 0, "v2 管理器兼容标记");
        RequireCount(normalized, "System message must be at the beginning.", 0, "旧 system-order 异常");
        RequireCount(normalized, "System and developer messages must precede conversation messages.", 0, "v2 continuation-order 异常");
        RequireCount(normalized, NormalizeRawLiteral(V3NoMessages), 1, "v3 空消息检查区");
        RequireCount(normalized, NormalizeRawLiteral(V3ToolsSystem), 1, "v3 tools instruction 合并区");
        RequireCount(normalized, NormalizeRawLiteral(V3LoopContent), 1, "v3 主循环 instruction 跳过区");
        int simpleSystemCount = CountOccurrences(normalized, NormalizeRawLiteral(V3PlainSimpleSystem));
        int reasoningSystemCount = CountOccurrences(normalized, NormalizeRawLiteral(V3PlainReasoningSystem));
        if (simpleSystemCount + reasoningSystemCount != 1)
        {
            throw new InvalidDataException($"Unsupported Template：v3 普通 instruction 合并区不匹配；simple={simpleSystemCount}, reasoning={reasoningSystemCount}。");
        }

        RequireCount(normalized, NormalizeRawLiteral(V3LoopSystem), 1, "v3 instruction 消费分支");
    }

    private static string RecreateV2(string template)
    {
        if (template.Contains(V2Marker, StringComparison.Ordinal))
        {
            ValidateKnownV2Template(template);
            return template;
        }

        if (template.Contains(GenericMarker, StringComparison.Ordinal))
        {
            throw new InvalidDataException("无法从其他管理器规则模板重建 v2；需要原始 GGUF 模板。");
        }

        return PatchExactQwenTemplateV2(template);
    }

    private static string RecreateV3(string template)
    {
        if (template.Contains(V3Marker, StringComparison.Ordinal))
        {
            ValidateKnownV3Template(template);
            return template;
        }

        if (template.Contains(V2Marker, StringComparison.Ordinal))
        {
            return UpgradeExactV2Template(template);
        }

        if (template.Contains(GenericMarker, StringComparison.Ordinal))
        {
            throw new InvalidDataException("无法从未知管理器规则模板重建 v3。");
        }

        return PatchExactQwenTemplate(template);
    }

    private static PromptTemplateRepairPreview Preview(
        PromptTemplateRepairStatus status,
        string detail,
        string template,
        string ruleVersion) => new(status, detail, template, ComputeSha(template), ruleVersion);

    private static void ValidateAnalysisHash(GgufChatTemplateAnalysis analysis)
    {
        string computedOriginalSha = ComputeSha(analysis.ChatTemplate);
        if (!computedOriginalSha.Equals(analysis.TemplateSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("GGUF analysis 中的原模板 SHA-256 与模板正文不一致；请重新分析。");
        }
    }

    private static string ComputeSha(string template) => Convert.ToHexString(SHA256.HashData(Utf8NoBom.GetBytes(template)));

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

    private static string Normalize(string template, string newLine) => newLine == "\n" ? template : template.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string RestoreNewLine(string template, string newLine) => newLine == "\n" ? template : template.Replace("\n", "\r\n", StringComparison.Ordinal);

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
        if (safe.Length > 80)
        {
            safe = safe[..80];
        }

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
        5. Manually unload and reload that model. This APPLY.md is the manual fallback; the main Switch flow can instead perform a previewed transactional runtime reload for supported failure codes.
        6. Return to Codex Multi-Model Manager and click **重新检测 Codex 指令层级**.
        7. Do not switch Codex until Basic Control, Leading Developer, Conversation Control and Continuation Developer all pass.

        To undo the change, disable/remove the per-model Prompt Template override in LM Studio and reload the model. The original GGUF is never modified.
        """;
}
