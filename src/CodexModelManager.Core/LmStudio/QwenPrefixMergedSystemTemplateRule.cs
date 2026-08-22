using System.Text;

namespace CodexModelManager.Core.LmStudio;

internal static class QwenPrefixMergedSystemTemplateRule
{
    public const string Marker = "{# CMM-CODEX-INSTRUCTION-HIERARCHY qwen-interleaved-instructions-v3 #}";

    private const string ResourceName = "CodexModelManager.Core.LmStudio.Templates.qwen-prefix-merged-system-source.jinja";
    private const string SourceCandidateAnchor = "{%- set sysns = namespace(count=0, text='') %}";

    private const string SourceInstructionMerge = """
        {%- if not messages %}
            {{- raise_exception('No messages provided.') }}
        {%- endif %}
        {%- set sysns = namespace(count=0, text='') %}
        {%- for message in messages %}
            {%- if sysns.count == loop.index0 and (message.role == 'system' or message.role == 'developer') %}
                {%- set sys_content = render_content(message.content, false, true)|trim %}
                {%- if sys_content %}
                    {%- set sysns.text = sysns.text + ('\n' if sysns.text else '') + sys_content %}
                {%- endif %}
                {%- set sysns.count = sysns.count + 1 %}
            {%- endif %}
        {%- endfor %}
        {%- set num_sys = sysns.count %}
        {%- set merged_system = sysns.text %}
        """;

    private const string TargetInstructionMerge = """
        {%- if not messages %}
            {{- raise_exception('No messages provided.') }}
        {%- endif %}
        {# CMM-CODEX-INSTRUCTION-HIERARCHY qwen-interleaved-instructions-v3 #}
        {%- set cmm_instruction_state = namespace(text='') %}
        {%- for instruction in messages %}
            {%- if instruction.role == 'system' or instruction.role == 'developer' %}
                {%- set instruction_content = render_content(instruction.content, false, true)|trim %}
                {%- if instruction_content %}
                    {%- set cmm_instruction_state.text = cmm_instruction_state.text + ('\n\n' if cmm_instruction_state.text else '') + instruction_content %}
                {%- endif %}
            {%- endif %}
        {%- endfor %}
        {%- set merged_system = cmm_instruction_state.text %}
        """;

    private const string SourceConversationStart = """
        {%- for message in messages %}
            {%- if loop.index0 >= num_sys %}
            {%- set content = render_content(message.content, true)|trim %}
            {%- if message.role == "system" or message.role == "developer" %}
                {{- raise_exception('System message must be at the beginning.') }}
        """;

    private const string TargetConversationStart = """
        {%- for message in messages %}
            {%- if message.role == "system" or message.role == "developer" %}
                {%- set content = '' %}
            {%- else %}
                {%- set content = render_content(message.content, true)|trim %}
            {%- endif %}
            {%- if message.role == "system" or message.role == "developer" %}
                {{- '' }}
        """;

    private const string SourceConversationEnd = """
            {%- endif %}
            {%- endif %}
        {%- endfor %}
        {%- if add_generation_prompt %}
        """;

    private const string TargetConversationEnd = """
            {%- endif %}
        {%- endfor %}
        {%- if add_generation_prompt %}
        """;

    private static readonly Lazy<string> CanonicalSource = new(LoadCanonicalSource);
    private static readonly Lazy<string> CanonicalTarget = new(CreateCanonicalTarget);

    public static bool IsSourceCandidate(string template) =>
        !string.IsNullOrWhiteSpace(template) &&
        (template.Contains(SourceCandidateAnchor, StringComparison.Ordinal) ||
         template.Contains("{%- set num_sys = sysns.count %}", StringComparison.Ordinal));

    public static bool IsPatchedCandidate(string template) =>
        !string.IsNullOrWhiteSpace(template) &&
        template.Contains(Marker, StringComparison.Ordinal) &&
        template.Contains("{%- set cmm_instruction_state = namespace(text='') %}", StringComparison.Ordinal);

    public static string Patch(string template)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(template);
        string newLine = DetectNewLine(template);
        string normalized = Normalize(template, newLine);
        ValidateSourceNormalized(normalized);
        string patched = ReplaceExact(
            ReplaceExact(
                ReplaceExact(normalized, NormalizeLiteral(SourceInstructionMerge), NormalizeLiteral(TargetInstructionMerge), "prefix instruction 合并区"),
                NormalizeLiteral(SourceConversationStart),
                NormalizeLiteral(TargetConversationStart),
                "conversation instruction 分支"),
            NormalizeLiteral(SourceConversationEnd),
            NormalizeLiteral(TargetConversationEnd),
            "conversation loop 尾部");
        ValidatePatchedNormalized(patched);
        return RestoreNewLine(patched, newLine);
    }

    public static void ValidatePatched(string template)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(template);
        string newLine = DetectNewLine(template);
        ValidatePatchedNormalized(Normalize(template, newLine));
    }

    private static void ValidateSourceNormalized(string normalized)
    {
        ValidateSharedAnchors(normalized);
        RequireCount(normalized, NormalizeLiteral(SourceInstructionMerge), 1, "prefix system/developer 聚合区");
        RequireCount(normalized, NormalizeLiteral(SourceConversationStart), 1, "num_sys 主循环保护与拒绝分支");
        RequireCount(normalized, NormalizeLiteral(SourceConversationEnd), 1, "主循环尾部");
        RequireCount(normalized, Marker, 0, "管理器 v3 标记");
        if (!normalized.Equals(CanonicalSource.Value, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Unsupported Template：Qwen prefix-merged-system 源模板存在未识别的非目标变化；未生成猜测模板。");
        }
    }

    private static void ValidatePatchedNormalized(string normalized)
    {
        ValidateSharedAnchors(normalized);
        RequireCount(normalized, NormalizeLiteral(TargetInstructionMerge), 1, "v3 全量 instruction 合并区");
        RequireCount(normalized, NormalizeLiteral(TargetConversationStart), 1, "v3 conversation instruction 跳过区");
        RequireCount(normalized, NormalizeLiteral(TargetConversationEnd), 1, "v3 主循环尾部");
        RequireCount(normalized, Marker, 1, "管理器 v3 标记");
        RequireCount(normalized, "System message must be at the beginning.", 0, "旧 system-order 异常");
        RequireCount(normalized, "{%- set num_sys = sysns.count %}", 0, "旧 num_sys 前缀计数");
        RequireCount(normalized, SourceCandidateAnchor, 0, "旧 sysns 前缀聚合");
        if (!normalized.Equals(CanonicalTarget.Value, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Unsupported Template：Qwen prefix-merged-system v3 模板存在未识别的非目标变化。");
        }
    }

    private static void ValidateSharedAnchors(string normalized)
    {
        RequireCount(normalized, "{%- macro render_content(content, do_vision_count, is_system_content=false) %}", 1, "render_content 宏");
        RequireCount(normalized, "<|vision_start|><|image_pad|><|vision_end|>", 1, "image vision 分支");
        RequireCount(normalized, "<|vision_start|><|video_pad|><|vision_end|>", 1, "video vision 分支");
        RequireCount(normalized, "{%- set reasoning_instructions = '' %}", 1, "reasoning instructions 区");
        RequireCount(normalized, "{%- if tools and tools is iterable and tools is not mapping %}", 1, "tools system 区");
        RequireCount(normalized, "{%- if merged_system %}", 2, "merged_system 输出区");
        RequireCount(normalized, "{%- for message in messages[::-1] %}", 1, "反向 conversation 扫描");
        RequireCount(normalized, "{%- elif message.role == \"user\" %}", 1, "user 分支");
        RequireCount(normalized, "{%- elif message.role == \"assistant\" %}", 1, "assistant 分支");
        RequireCount(normalized, "{%- elif message.role == \"tool\" %}", 1, "tool response 分支");
        RequireCount(normalized, "{%- if message.reasoning_content is string %}", 1, "assistant reasoning 分支");
        RequireCount(normalized, "{%- if message.tool_calls and message.tool_calls is iterable and message.tool_calls is not mapping %}", 1, "tool-call 分支");
        RequireCount(normalized, "{%- if add_generation_prompt %}", 1, "generation-prompt 分支");
        RequireCount(normalized, "{#- Unsloth fixes - developer role, merged system messages, tool calling #}", 1, "模板尾部结构标记");
    }

    private static string CreateCanonicalTarget()
    {
        string source = CanonicalSource.Value;
        return ReplaceExact(
            ReplaceExact(
                ReplaceExact(source, NormalizeLiteral(SourceInstructionMerge), NormalizeLiteral(TargetInstructionMerge), "canonical prefix instruction 合并区"),
                NormalizeLiteral(SourceConversationStart),
                NormalizeLiteral(TargetConversationStart),
                "canonical conversation instruction 分支"),
            NormalizeLiteral(SourceConversationEnd),
            NormalizeLiteral(TargetConversationEnd),
            "canonical conversation loop 尾部");
    }

    private static string LoadCanonicalSource()
    {
        using Stream stream = typeof(QwenPrefixMergedSystemTemplateRule).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidDataException("内置 Qwen prefix-merged-system 结构基线资源缺失。");
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, false);
        string source = reader.ReadToEnd();
        string newLine = DetectNewLine(source);
        return Normalize(source, newLine);
    }

    private static string ReplaceExact(string text, string source, string target, string description)
    {
        RequireCount(text, source, 1, description);
        return text.Replace(source, target, StringComparison.Ordinal);
    }

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

    private static string Normalize(string template, string newLine) =>
        newLine == "\n" ? template : template.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string RestoreNewLine(string template, string newLine) =>
        newLine == "\n" ? template : template.Replace("\n", "\r\n", StringComparison.Ordinal);

    private static string NormalizeLiteral(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal);
}
