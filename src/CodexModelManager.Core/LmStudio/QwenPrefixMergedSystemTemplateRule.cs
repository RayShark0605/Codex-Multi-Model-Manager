using System.Text;

namespace CodexModelManager.Core.LmStudio;

/// <summary>
/// Qwen「前缀合并 system」模板修复规则（v3）：
/// 以固定锚点把“仅开头合并 system/developer”的源模板改写为“全量交错指令合并”的目标模板，
/// 源与目标都必须与内置基线逐字节一致，任何未识别变化都会拒绝修补（绝不生成猜测模板）。
/// </summary>
internal static class QwenPrefixMergedSystemTemplateRule
{
    /// <summary>目标模板里的管理器标记（规则版本 qwen-interleaved-instructions-v3）。</summary>
    public const string Marker = "{# CMM-CODEX-INSTRUCTION-HIERARCHY qwen-interleaved-instructions-v3 #}";

    private const string ResourceName = "CodexModelManager.Core.LmStudio.Templates.qwen-prefix-merged-system-source.jinja";
    private const string SourceCandidateAnchor = "{%- set sysns = namespace(count=0, text='') %}";

    // 源模板：只在消息列表开头聚合连续的 system/developer（sysns 前缀计数）
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

    // 目标模板：聚合全部 system/developer 指令（允许交错），带 v3 标记
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

    // 源模板：num_sys 之后出现 system/developer 即抛错（旧顺序约束）
    private const string SourceConversationStart = """
        {%- for message in messages %}
            {%- if loop.index0 >= num_sys %}
            {%- set content = render_content(message.content, true)|trim %}
            {%- if message.role == "system" or message.role == "developer" %}
                {{- raise_exception('System message must be at the beginning.') }}
        """;

    // 目标模板：交错的 system/developer 输出空内容（已并入 merged_system）
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

    // 源模板主循环尾部（比目标多一层 endif）
    private const string SourceConversationEnd = """
            {%- endif %}
            {%- endif %}
        {%- endfor %}
        {%- if add_generation_prompt %}
        """;

    // 目标模板主循环尾部
    private const string TargetConversationEnd = """
            {%- endif %}
        {%- endfor %}
        {%- if add_generation_prompt %}
        """;

    private static readonly Lazy<string> CanonicalSource = new(LoadCanonicalSource);
    private static readonly Lazy<string> CanonicalTarget = new(CreateCanonicalTarget);

    /// <summary>判断模板是否为本规则的“可修补源”（识别 sysns 前缀聚合特征）。</summary>
    public static bool IsSourceCandidate(string template) =>
        !string.IsNullOrWhiteSpace(template) &&
        (template.Contains(SourceCandidateAnchor, StringComparison.Ordinal) ||
         template.Contains("{%- set num_sys = sysns.count %}", StringComparison.Ordinal));

    /// <summary>判断模板是否已是本规则的 v3 修补结果。</summary>
    public static bool IsPatchedCandidate(string template) =>
        !string.IsNullOrWhiteSpace(template) &&
        template.Contains(Marker, StringComparison.Ordinal) &&
        template.Contains("{%- set cmm_instruction_state = namespace(text='') %}", StringComparison.Ordinal);

    /// <summary>
    /// 修补模板：统一换行后在归一化文本上做三段精确替换（指令合并区、会话分支、循环尾部），
    /// 校验通过后还原原换行风格返回。
    /// </summary>
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

    /// <summary>校验模板已是合法的 v3 修补结果（不修改内容）。</summary>
    public static void ValidatePatched(string template)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(template);
        string newLine = DetectNewLine(template);
        ValidatePatchedNormalized(Normalize(template, newLine));
    }

    /// <summary>校验源模板：共享锚点、三段源片段各出现一次、无 v3 标记，且与内置基线完全一致。</summary>
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

    /// <summary>校验修补结果：共享锚点、三段目标片段各一次、v3 标记一次、旧结构清零，且与基线目标一致。</summary>
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

    /// <summary>校验源/目标共有的结构性锚点各自出现指定次数，确保模板族正确。</summary>
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

    /// <summary>由基线源模板推导基线目标模板（同样三段替换）。</summary>
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

    /// <summary>从程序集内嵌资源加载基线源模板并归一化换行。</summary>
    private static string LoadCanonicalSource()
    {
        using Stream stream = typeof(QwenPrefixMergedSystemTemplateRule).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidDataException("内置 Qwen prefix-merged-system 结构基线资源缺失。");
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, false);
        string source = reader.ReadToEnd();
        string newLine = DetectNewLine(source);
        return Normalize(source, newLine);
    }

    /// <summary>精确替换：源片段必须恰好出现一次，替换后返回新文本。</summary>
    private static string ReplaceExact(string text, string source, string target, string description)
    {
        RequireCount(text, source, 1, description);
        return text.Replace(source, target, StringComparison.Ordinal);
    }

    /// <summary>断言子串出现次数恰为期望值，否则抛出带说明的校验异常。</summary>
    private static void RequireCount(string text, string value, int expected, string description)
    {
        int actual = CountOccurrences(text, value);
        if (actual != expected)
        {
            throw new InvalidDataException($"Unsupported Template：{description} 应匹配 {expected} 次，实际 {actual} 次；未生成猜测模板。");
        }
    }

    /// <summary>统计子串出现次数（允许重叠定位但不重叠计数）。</summary>
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

    /// <summary>探测模板换行风格；CRLF 与孤立 LF 混用时拒绝修补以保持原文。</summary>
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

    /// <summary>把模板换行归一为 LF（已是 LF 则原样返回）。</summary>
    private static string Normalize(string template, string newLine) =>
        newLine == "\n" ? template : template.Replace("\r\n", "\n", StringComparison.Ordinal);

    /// <summary>把 LF 换行还原为原风格（原为 LF 则原样返回）。</summary>
    private static string RestoreNewLine(string template, string newLine) =>
        newLine == "\n" ? template : template.Replace("\n", "\r\n", StringComparison.Ordinal);

    /// <summary>内嵌字面量统一按 LF 处理（与归一化后的模板可比）。</summary>
    private static string NormalizeLiteral(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal);
}
