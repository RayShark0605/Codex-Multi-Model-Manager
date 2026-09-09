# 2026-09-08：提高切换成功率的缺陷修复矩阵

## 目标、基线与范围

从干净 `master @ c7cba1f` 开始。修复前 Core **372 PASS / 0 FAIL / 8 opt-in SKIP**，App **14 PASS / 0 FAIL**；原基线位于 `artifacts/test-results/audit-plan-2026-09-08`。既有全绿并未覆盖下表的遗漏路径，调查阶段使用 Release 程序集及临时输入复现，不以源码猜测代替运行证据。

目标是尽可能成功切换：保留四阶段指令兼容性硬门槛；不增设 SSE、工具或 Level 3 门槛；一次合并确认后执行已知模板修复及有界状态恢复。不盲目重复 `/load`、`/unload`，不以收紧旧模板识别范围代替修复。

本次所有验收只使用临时目录、fake HTTP、受控进程、内存协议输入和不显示窗口的 STA 控件。未启动真实 GUI、未写真实 Codex 配置/凭据/LM Studio defaults、未调用真实模型重载或推理。历史现场记录保持原日期，不转换为本轮现场 PASS。

## 修复与回归保护

| 问题与最小复现输入/状态 | 根因与修复 | 主要回归 |
|---|---|---|
| `[model_providers."lmstudio.custom"]` 与受控 `lmstudio` 共存，删除受控表误删带点 sibling | 使用 Tomlyn syntax tree 的 decoded key segments 与 source span；不再用显示路径前缀或逐行正则判归属 | `ConfigSafetyAuditTests.ManagedRemovalPreservesQuotedDottedProviderSiblings` / `EscapedTomlKeyAndLiteralDotsRemainDistinct` |
| 注释含三引号，后有未知表；根 model/provider 使用合法多行字符串 | 注释/字符串词法由解析器处理；替换整个值 span，保持非目标字节/注释/BOM/换行；runtime unquote 使用同一 TOML 字符串语义 | `QuoteTextDoesNotHideTheNextUnknownTable` / `MultilineRootValueUsesItsEntireSpanAndPreservesOtherBytes` / `MultilineProviderTokenSurvivesProviderStateRoundTrip` |
| Secondary quoted `]` 表头、escaped key、重复 array-table；只选其中一项 | 扫描/补丁共享 source document；canonical selection key 保持兼容；无法唯一定位的 array-table 条目只显示不批改 | `QuotedClosingBracketSecondarySelectionOnlyChangesItsOwnValue` / `ArrayTableOverridesAreVisibleButCannotBeSelectedForImplicitBatchEditing` |
| `profiles.A`、`profiles.a` 同时存在且分别勾选/还原 | 路径部分按 Windows 忽略大小写，TOML key 部分按 Ordinal；修复整个选择字符串使用忽略大小写 comparer 的碰撞 | `SecondarySelectionAndRestoreKeepCaseDistinctTomlKeysSeparate` |
| staging 首次写入失败/取消；新建候选回滚时目标只读、删除失败 | 第一次 I/O 前登记临时文件所有权；新目标严格删除并验证不存在；原始故障在前、回滚故障在后聚合，不吞恢复失败 | `StageFailureOrCancellationCleansEveryRegisteredCandidate` / `FailedDeletionOfNewCandidateIsReportedAsRollbackFailure` |
| LM 修复失败后的取证 GET 超时或日志抛错；备份还未完成 | 取证独立 5 秒预算，原始故障与阶段先保存在内存；取证/日志不能跳过独立恢复；尚未发 patch load 不猜测唯一原实例为补丁 | `PersistentBackupFailureDoesNotUnloadOriginalInstance` / `FailureEvidenceTimeoutAndBrokenLoggerCannotSkipPersistentRollback` |
| 原模型已恢复，仅最后验证短暂失败；再次恢复 | 统一考虑 FailureStage / LastRecoveryFailureStage、前后实例和原四阶段签名；精确原状态只验证并关闭 journal，不重复重载 | `RestoredInstanceWithTransientFinalProbeFailureIsAdoptedOnNextRecovery` / 既有 schema-v1–v4 恢复矩阵 |
| 瞬态网络/内部超时、408/429/502/503/504；POST 响应丢失但已执行 | 已确认 Switch 共享一个重试额度，重试前 guard + native 完整状态核对；完整重跑四阶段，不拼接样本；已生效 POST 不重复；取消/确定性错误/歧义不重试 | `SwitchRetryBudgetTests` / `LmStudioIdentityAndRetryTests` / `ConfirmedLoadRetryReconcilesStateBeforeConsideringAnotherPost` / `UnloadTransientFailureReconcilesOriginalStateAndNeverBlindlyRepeats` |
| 模板修复先确认、修复后又要求配置确认；新实例 ID 造成重规划 | 提前生成只读配置预览，合并确认；Commit 仍重复 live 四阶段硬门槛；目标 ID 之外语义变化或确认读集变化要求重新预览 | `RepairConfirmationAuditTests` / `SelectionAuditTests.RepairConfirmationOnlyAllowsReturnedInstanceIdToChange` |
| 忙碌期间 Provider 改动被丢弃；晚完成的旧 catalog 覆盖新选择；两个页面选不同实例 | selection revision 合并最后一次选择、拒绝过期发布；事务冻结输入；统一同步 context/compact/reasoning/模板状态；请求再次验证 Provider/model/endpoint/instance | `SelectionAuditTests` 及既有 App lifetime 测试 |
| 补丁成功后取消导致 UI 用已取消 token 回滚；日志异常发生在“保留已提交补丁”标记之前 | UI 后续失败回滚也使用独立预算；安全状态先于 UI/日志副作用；已提交/结果不确定时不卸载可能仍被配置引用的实例 | `SelectionAuditTests` 的独立回滚与 committed state 顺序用例 |
| catalog 临时 fallback 到其他实例，丢失已确认 reasoning；同实例刷新重复清掉 GGUF | 新实例绑定显式保留并验证原 reasoning/compact；不将列表重建视为模型切换；避免协调器重复刷新；选择相关诊断冻结输入 | `SelectionAuditTests` 的 same-instance refresh / reloaded reasoning 用例 |
| 父进程先退出，后代继续持有 stdout/stderr；无限输出 | 共享 runner 的 deadline 覆盖退出和 drain，限制累计输出，finally 有界清理，UTF-8；smoke 增量解析 | `ProcessAndProtocolRemediationTests` |
| SSE error、仅 DONE、非法 JSON、半帧被识别成功；失败工具事件满足 L3 | 完整帧增量解析，首个合法非错误 Responses 事件早返回；L3 使用完成 envelope/ID/status/结果而非递归找名字 | `ProtocolEvidenceTests` / `ResponsesCompatibilityTests` / `ProviderRemediationTests` |
| native loaded entry 缺 ID、重复 ID | 不再从 source key 伪造 loaded ID；非法条目独立诊断，冲突组不可切换/推理，其他合法模型仍保留 | `InvalidNativeInstanceIdIsNotInventedAndValidSiblingRemainsAvailable` / `AllMembersOfDuplicateIdentityGroupAreUnavailableWithoutRejectingValidModel` |
| 备份 manifest `{}`、`files=null`、`files=[null]`、重复/越界路径 | 统一结构与条目校验；损坏快照按项 Invalid；Restore 在任何写入前拒绝 | `InvalidManifestShapeIsOneInvalidHistoryItemAndRestoreHasNoSideEffects` / `MalformedNonEmptyManifestFailsBeforeRestore` |
| 三引号 TOML/转义 JSON token 遮盖不完整；Tomlyn 原始错误含源文本 | 覆盖多行/escaped literal；regex 有超时且超时整条遮盖；解析错误只给固定分类和行列，不附原始 message/inner exception | `RedactionAuditTests` / `TomlDiagnosticDoesNotExposeOriginalSourceOrInnerExceptions` |
| 删除旧版后 promotion 失败；环境变量留在调用方；脏工作树 EXE 仅标 HEAD | 验证三个候选后保留旧目录，Mutex 串行提升，失败回退；拒绝越界/reparse；finally 恢复环境；source manifest + EXE ProductVersion 标内容身份 | `scripts/Test-PublishPromotion.ps1`；实际发布 manifest/EXE 哈希复核 |

## 兼容性与非目标

- 公开配置和 Provider ID、上下文公式、Manual 偏好、Secondary opt-in、OpenAI 登录保护、未知配置保留不变。没有升级 Tomlyn 或改变 journal schema。
- 旧 simple/reasoning Qwen 模板是受控片段与锚点检查/定向修补，不是完整模板语义认证；prefix-merged 仍要求完整 canonical 核验。不新增猜测模板或未经核验的 LM Studio 版本。
- 四阶段 PASS 只证明被测消息结构；不能据此承诺 MCP/工具、Plan→执行、长上下文或长期任务效果。
- 如果历史版本已经把大小写不同的 Secondary selection/state key 合并，现存文件无法推导丢失值；本次避免继续碰撞，不凭空补造历史偏好。
- 发布使用保留旧版的可恢复提升，不宣称跨目录 rename 具备数据库级原子性。进程/机器在提升中途退出时，保留的 `previous-*` 目录是人工恢复证据，不自动删除。
- 未测量真实模型切换成功率或整体提速比例。本轮验证 deadline、内存上限、取消行为及避免不必要重载。

## 复跑与证据

在仓库根目录运行，先移除进程中的 `CMM_RUN_LIVE_*` 与 `CMM_LIVE_GGUF_PATH`/`CMM_LIVE_GGUF_TEMPLATE_SHA`，不要启用现场开关：

```powershell
dotnet build .\CodexModelManager.sln -c Release --no-restore
dotnet test .\tests\CodexModelManager.Tests\CodexModelManager.Tests.csproj -c Release --no-build --no-restore
dotnet test .\tests\CodexModelManager.App.Tests\CodexModelManager.App.Tests.csproj -c Release --no-build --no-restore
.\scripts\Test-PublishPromotion.ps1
dotnet build .\CodexModelManager.sln -c Debug --no-restore
dotnet format .\CodexModelManager.sln --verify-no-changes --no-restore
git diff --check
# 普通测试单独完成后，避免重复回归：
.\publish.ps1 -SkipTests
```

本轮原始 TRX、构建/格式/发布日志和最终工件汇总保存在 `artifacts/test-results/repair-2026-09-08`。最终结果与工件 SHA 以 `docs/VERIFICATION.md` 的同日条目及 `artifacts/publish/win-x64/source-manifest.json` 为准；不把基线或中间失败日志当最终 PASS。
