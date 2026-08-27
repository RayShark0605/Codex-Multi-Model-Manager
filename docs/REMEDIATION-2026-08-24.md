# 2026-08-24 代码审阅修复矩阵

基线：`master @ 942007ea6c0b`。本文记录外部审阅报告中 M1–M6 与低危 #1–#40 的逐项判定、复核证据、最终处理和自动化覆盖。产品行为以实际执行路径和隔离回归为准；源码说明仅用于解释该行为。

状态定义：

- **Accepted**：缺陷可以在当前代码路径或受控运行时中触发，本轮修复。
- **Partially Accepted**：现象的一部分成立，但按安全边界采用了更窄的修复。
- **Rejected**：报告所述故障结果不成立，或所述行为是有意的 fail-closed/迁移契约；不修改产品逻辑，并尽可能增加回归证据。

本轮没有启动可见主程序，没有执行真实 Provider 切换、Credential Manager 写入或 LM Studio `/load`、`/unload`。Core 回归使用临时目录、fake HTTP/process/provider；App 回归使用不显示窗口的专用 STA 线程。

## 中等问题 M1–M6

| 编号 | 判定 | 复核证据 | 修复 | 主要回归测试 |
|---|---|---|---|---|
| M1 | **Accepted** | WinForms `DockStyle.Top` 按 Controls 逆序布局；原两页后加入 table，实际会先占据顶部。 | `CurrentSwitchControl`、`LmStudioControl` 均改为先加入 table、后加入 buttons；不改另外三个正确页面。 | `MainOperationPanelsDockAboveTheirTables` 在 STA 中检查实际 Bounds。 |
| M2 | **Accepted** | 两个同步凭据事件可让 `Win32Exception` 逃到 UI 线程；`CatchException` 模式原先没有 `ThreadException` 订阅，启动 catch 也直接显示原始消息。 | 保存和状态刷新统一进入 `RunUiActionAsync`；composition 期间注册/退订全局处理器；`UiExceptionReporter` 先记录日志，再通过同一 `SecretRedactor` 显示类型及脱敏消息，处理器失败时只显示固定兜底文案。 | `CredentialWriteFailureUsesUnifiedUiActionErrorPath`、`CredentialStatusRefreshFailureUsesUnifiedUiActionErrorPath`、`UiExceptionReporterLogsAndRedactsBeforeShowing`、`ProgramThreadExceptionHandlerUsesTheSafeReporter`。 |
| M3 | **Accepted** | 原 linked CTS 只传给 `SendAsync`，响应体读取收到原始 token，响应头后停滞不受单请求 45 秒约束。 | 同一个 linked token 覆盖响应头和限长响应体读取；取消/超时稳定映射到当前 probe 失败。 | `HierarchyTimeoutCoversAStalledResponseBody`。 |
| M4 | **Accepted** | `JsonElement.TryGetProperty` 在非 Object 根上抛 `InvalidOperationException`，原 MCP 主循环只捕获 `JsonException`。 | 在读取 `id`、`method`、`params`、tool name 前逐层检查 `ValueKind`；非对象或字段类型错误不会终止进程。 | `TestMcpServerSurvivesNonObjectJsonAndThenAnswersPing` 连续发送数组、字符串、数字、null 和错类型字段后再验证 `cmm_ping`。 |
| M5 | **Accepted** | UTF-8 子进程输出被非 UTF-8 父代码页解码时，中文路径可稳定损坏；默认编码依赖宿主环境。 | 所有已知 UTF-8 CLI 的 redirected stdin/stdout/stderr 显式设为 `Encoding.UTF8`，覆盖 lms locator/endpoint、Codex app-server/smoke 等启动点。 | `CodexProcessReaderPreservesUtf8NonAsciiOutputIndependentOfParentCodePage`、`LmsProcessStartInformationUsesUtf8ForEveryRedirectedStream`。 |
| M6 | **Accepted** | 相对 `downloadsFolder` 抛出的 `InvalidDataException` 不在既有映射集合中，两个解析入口均可能泄漏内部异常。 | `ResolveAsync` 与 `ResolvePsFromJson` 均把相对目录稳定归类为 `InvalidSettings`，保留 fail-closed 行为。 | `RelativeDownloadsFolderMapsToInvalidSettingsInBothLocatorPaths`。 |

## 低危问题 #1–#40

| 编号 | 判定 | 复核证据与处理 | 主要回归测试 |
|---|---|---|---|
| #1 | **Rejected** | 当前 Tomlyn 2.10.1 在补丁解析阶段直接拒绝裸 CR，错误为 `Invalid \\r not followed by \\n`；文本 span 替换和写盘均不可达。产品逻辑不改。 | `BareCarriageReturnTomlIsRejectedBeforePatching` 固化“补丁前拒绝且原文不变”。 |
| #2 | **Accepted** | 原根键白名单不识别合法 quoted key。根赋值左侧现由 `TomlDottedKey.ParseSegments` 解码；仅一个逻辑段才是根键，并保留原引号、空格、注释与换行；`"a.b"` 不会误匹配 `model`。 | `QuotedRootKeysAreUpdatedInPlaceWithoutMatchingQuotedDots`。 |
| #3 | **Accepted** | 命名 Semaphore 不具备进程死亡后的 abandonment 恢复语义。改为命名 Mutex，并在专用长运行线程完成 wait、异步事务同步桥接和 release；`AbandonedMutexException` 表示取得所有权，仍执行完整指纹检查。旧 Semaphore 同名对象造成类型冲突时给出“关闭旧版本实例”的明确诊断。 | `AbandonedMutexOwnershipIsRecoveredAndAllChecksStillRun`、`ConcurrentWritersUsingTheSameMutexAreSerialized`。 |
| #4 | **Accepted** | 原 rollback 异常会覆盖提交根因。现保留原异常堆栈；rollback 失败时按“原始故障、回滚故障”顺序抛 `AggregateException`，并保留 rollback 文件。 | `RollbackFailurePreservesPrimaryAndRollbackEvidenceInOrder`。 |
| #5 | **Accepted** | `DriveInfo` 不接受 UNC 根。磁盘空间查询抽象为内部 provider；本地盘继续用 `DriveInfo`，Windows UNC 使用 `GetDiskFreeSpaceExW`，Win32 失败包装为本地化 `IOException`。 | `UncSpaceCheckUsesInjectedProviderWithoutConstructingDriveInfo`。 |
| #6 | **Accepted** | `ConfigureLmStudio` 和候选语义校验对 null compact 的契约不一致。请求现在在 preflight、计划 hash、候选生成前一次性标准化；null/Automatic/Manual 的最终有效值全程一致，冲突输入 fail closed。 | `LmStudioNullAndExplicitCompactContractsNormalizeDeterministically`、`SwitchPlanAndPersistedPreferenceUseTheSameNormalizedValues`。 |
| #7 | **Accepted** | `Contains("key")` 确会误伤 `monkey`、`keyboard`。现对名称 percent-decode、大小写归一化并按分隔符/完整敏感名称识别，不再做任意字母子串匹配。 | `SensitiveQueryDetectionUsesTokenBoundaries`。 |
| #8 | **Accepted** | 损坏或显式 null 的 appsettings 会让启动持续失败。新增 `AppSettingsLoadResult`/`LoadWithRecoveryAsync`：原始字节唯一隔离、flush、SHA-256 复核、删除前完整指纹复核；并发漂移则重读，普通 I/O 故障继续抛出。UI 只警告隔离路径、哈希和异常类型。 | `CorruptSettingsAreQuarantinedByteForByteAndDefaulted`、`ConcurrentValidSettingsReplacementIsNeverDeletedDuringRecovery`、`SameByteConcurrentRewriteIsDetectedByFullFingerprintBeforeDeletion`、`OrdinarySettingsIoFailureIsNotReportedAsRecovery`。 |
| #9 | **Rejected** | schema v1 没有保存 Automatic/Manual 来源；“值精确等于旧公式则迁移为 Automatic”是既有文档化策略，且不存在可无损恢复来源的替代判据。产品逻辑不改，既有迁移测试继续锁定该契约。 | 既有 `AppSettingsRepositoryTests` v1→v2 迁移测试。 |
| #10 | **Accepted** | request 未给 limit 时可能持久化为 `Manual + null`。与 #6 共用标准化路径，计划请求和 `ModelPreference` 只保存最终有效 mode/limit。 | `SwitchPlanAndPersistedPreferenceUseTheSameNormalizedValues`。 |
| #11 | **Accepted** | `ProbeAsync` 原先无条件吞 `TaskCanceledException`。现在只有内部 timeout 映射为“未连接”，调用方取消原样传播。 | `LmStudioProbePropagatesCallerCancellation`。 |
| #12 | **Accepted** | native models HTTP/shape 成功但空列表时，继续 fallback 会把健康空状态改写成聚合失败。现在立即返回空列表且 `UsedFallback=false`。 | `NativeModelsSuccessWithEmptyListDoesNotProbeFallbackRoutes`。 |
| #13 | **Accepted** | 生成诊断异常时使用调用方 token，取消可能覆盖 `LmStudioApiException` 和 `LastApiFailure`。已取得响应的限长诊断读取改用 `CancellationToken.None`；未知原始 body 仍不持久化。 | `LmStudioClientTests` 的 API failure/诊断回归与本轮 cancellation suite。 |
| #14 | **Rejected** | `SameSourceInstanceIdsBeforeLoad` 的采样点在原实例已卸载、补丁实例尚未加载之间；此时空集合是正确证据，事务前实例完整保存在 `OriginalInstance`。不修改字段语义。 | 既有 `LmStudioInstanceControllerTests` journal/rollback 取证测试。 |
| #15 | **Rejected** | lease 跨越 `Apply → Complete/Rollback` 是故意的 fail-closed 事务边界，不能在 Apply 后提前释放。本轮仅通过 #36 与 #40 收紧 controller 所有权及关窗闭环。 | `PlannerOwnershipHelperUnsubscribesAndDisposesOnFailure`、`ClosePreparationWaitsForActiveActionAndRejectsNewActions`。 |
| #16 | **Accepted** | `lms server status` 启动/损坏/锁定失败原会越过 configured/default fallback。启动故障现在返回 null，由既有 endpoint/default 逻辑接管；调用方取消仍传播。 | `LmStudioEndpointDetectorTests` 启动/fallback 回归；`LmsProcessStartInformationUsesUtf8ForEveryRedirectedStream` 覆盖统一启动描述。 |
| #17 | **Accepted** | 原正则只认窄格式。解析器现支持自然语言、`port: N`、`port=N`、loopback URI 和 JSON `"port": N`；只接受 1–65535，出现多个不同端口则返回 null。 | `LmsPortParserSupportsKnownFormatsAndRejectsConflicts`。 |
| #18 | **Accepted** | preflight 注释承诺 authoritative native，却调用带 fallback 的发现。现只调用 `DiscoverNativeModelsAsync`；native HTTP/schema 错误不可被 fallback 模型伪装为 instance missing。 | `SwitchPreflightDoesNotMaskNativeFailureWithFallbackData`。 |
| #19 | **Accepted** | 缺失 `ruleVersion` 可反序列化为 null 并在 planner 的实例 `.Equals` 处 NRE。store 增加必需字符串校验，planner 同时使用 null-safe `string.Equals`，统一产生 `InvalidDataException`。 | `NullRuleVersionIsRejectedAsInvalidJournalInsteadOfNullReference`。 |
| #20 | **Partially Accepted** | “任意 JSON 阻塞”对 `notes.json` 成立，但跳过所有坏文件会隐藏真实恢复事务。枚举现在只认 `<32位GUID>.json`；无关 JSON 忽略，GUID 命名但损坏的 journal 继续硬阻断。 | `UnrelatedJsonIsIgnoredButCorruptGuidJournalStillBlocks`。 |
| #21 | **Accepted** | crash 后的 transaction tmp 不被枚举也不清理。现在仅 best-effort 清理精确 `<GUID>.json.tmp-<GUID>` 且超过 24 小时的文件；新鲜或未知名称不动。 | `OnlyExactStaleTransactionTemporaryFilesAreCleaned`。 |
| #22 | **Accepted** | 尾分隔符使 `root + separator` 判断失配。清理改为基于规范化路径的严格后代关系，不再手拼分隔符，并保留 volume/UNC root 语义。 | `FailedExportCleansStrictChildWhenOutputRootHasTrailingSeparator`、`StrictDescendantCheckHandlesTrailingSeparatorsAndSiblingPrefixes`、`StrictDescendantCheckPreservesVolumeRootSemantics`。 |
| #23 | **Accepted** | 毫秒级目录名存在 TOCTOU 共享风险。导出目录改为“毫秒时间 + 完整 GUID”，并发调用永不共享目录。 | `ConcurrentExportsAlwaysUseDistinctCompleteDirectories`。 |
| #24 | **Accepted** | Windows 设备名不能作为普通 basename。按 OrdinalIgnoreCase 识别 `CON/PRN/AUX/NUL/COM1–9/LPT1–9/CONIN$/CONOUT$` 并加 `_` 前缀。 | `ExportPathSegmentProtectsWindowsDeviceNames`。 |
| #25 | **Accepted** | App-server 的 list/version catch 会吞调用方取消。现在区分调用方取消和内部 timeout；前者传播，不降级 cache/null。 | `CodexAppServerClientTests` cancellation/timeout 回归。 |
| #26 | **Accepted** | 后续阶段异常会追加第二条 `Responses: Failed`。兼容性客户端维护显式当前阶段，每个 capability 只生成一行；后续失败不改写已成功 Responses，未运行阶段补 `Untested`。 | `ToolBodyTimeoutIsAttributedOnlyToToolStage` 及既有 compatibility result 测试。 |
| #27 | **Accepted** | 原 streaming 使用 `ResponseContentRead` 并缓冲到关流。现在 `ResponseHeadersRead`、单阶段完整 timeout、最大 64 KiB，收到首个有效 `data:` 行即成功返回。 | `StreamingReturnsAfterFirstDataEventWithoutWaitingForConnectionClose`。 |
| #28 | **Accepted** | Responses/Hierarchy 多处对非 Object 2xx 直接 `TryGetProperty`，数字字段也可能对错误 kind 调用 `TryGetInt*`。所有根、output item、function arguments、reasoning content 及 Codex/LM/lms 数字字段均先验证 `ValueKind`；非法结构稳定归属当前阶段。 | `NonObjectReasoningJsonIsAStableStageFailure`、`NativeModelsRejectNonObjectRootWithoutInvalidOperationEscape`、`NativeModelsNumericFieldsRejectWrongKindsWithoutInvalidOperationEscape`、`AppServerModelNumericFieldsRejectWrongKindsWithoutInvalidOperationEscape`、`LmsPsNumericFieldsRejectWrongKindsWithoutInvalidOperationEscape`。 |
| #29 | **Accepted** | Responses endpoint/path 的公共边界不够防御。构造时验证稳定 base URI、补齐尾斜杠、拒绝 userinfo/query/fragment；`responsesPath` 必须为不能覆盖 authority 的相对路径。 | `ResponsesClientRejectsUnsafeEndpoint`、`ResponsesClientRejectsPathThatCanOverrideAuthority`、`ResponsesClientNormalizesBaseUriTrailingSlash`。 |
| #30 | **Accepted** | DeepSeek catalog 的非数组 modalities、重复 slug、非对象根和取消确有逃逸/误降级路径。现逐层验证 shape、字段类型和值域，slug Ordinal 唯一；调用方取消传播，仅网络/内部 timeout/无效下载按既有顺序回退已验证 cache/snapshot。 | `DeepSeekCatalogRejectsInvalidShapesAndDuplicateSlugs`、`DeepSeekCatalogPropagatesCallerCancellationInsteadOfUsingSnapshot`。 |
| #31 | **Accepted** | `Kill`/`HasExited` 竞态可覆盖主异常，reader drain 也可能无限等待；字符串 id 原不被接受。新增统一 bounded terminate/drain（终止后最多等待 2 秒即关闭本地管道，再有界观察 reader），清理错误不覆盖主故障；app-server 接受匹配数字 id 或其十进制字符串。 | `ProcessCleanupRemainsBoundedWhenAReaderNeverCompletes`、`ProcessCleanupToleratesAnAlreadyDisposedProcess`、`AppServerResponseIdAcceptsOnlyMatchingNumberOrDecimalString`。 |
| #32 | **Accepted** | 引用 agent config 的 I/O 会中断扫描，行正则也可能读取三引号正文伪赋值。Scanner/Patcher 共用 TOML 行级词法状态器；非主配置错误生成 `<scan_error>`，主配置仍阻断。 | `MultilineTomlStringPseudoOverridesAreNeitherScannedNorPatched`、`InvalidReferencedAgentConfigProducesScanErrorWithoutBlockingPrimary`、`MissingOrInaccessibleReferencedAgentConfigProducesScanError`。 |
| #33 | **Accepted** | `Version`/三段正则不具备 SemVer 预发布和缺失零段语义。`IsAtLeast` 的内部比较器支持 2–4 段、缺失零段等价、SemVer 预发布标识排序并忽略 build metadata；正式版高于同号 rc。 | `SemanticVersionHonorsMissingZeroAndPrereleaseOrdering`。 |
| #34 | **Accepted** | 三个边界均修复：process 信息先形成一次快照、每个进程只读一次 `MainModule`；locator 支持经过验证的 npm `node.exe + 相邻 codex.js` 参数化 invocation，WindowsApps alias 排后且不经 shell；reasoning effort 入口 trim + lower-case。 | `VerifiedNpmInvocationUsesNodeAndArgumentListWithoutShellConcatenation`、`ReasoningEffortIsTrimmedAndCanonicalizedCaseInsensitively`，以及既有 runtime probe 测试。 |
| #35 | **Accepted** | `NumericUpDown.Maximum=4,000,000` 会钳制真实更长 context 并永久阻断一致性校验。两个控件均提升为 `int.MaxValue`，原值显示和传递。 | `EightMillionContextIsNotClamped`。 |
| #36 | **Accepted** | planner 在返回 tuple 前抛出时，调用方拿不到已创建 controller，导致未退订/未 Dispose。资源现在创建后立即进入所有权 helper；只在成功时转移，失败必退订并释放。 | `PlannerOwnershipHelperUnsubscribesAndDisposesOnFailure`。 |
| #37 | **Accepted** | `OnLogMessage` 原只检查 `IsDisposed`。现在所有延迟封送统一检查 `IsDisposed`、`Disposing`、`IsHandleCreated`，并容忍句柄销毁竞态的 `InvalidOperationException`。 | `LogCallbackIsSafeBeforeHandleCreationAcrossThreadsAndAfterDisposal`。 |
| #38 | **Accepted** | 两个 selection sync 的 `updating` 无 finally，异常可永久关闭事件。均改为 `try/finally` 恢复门控。 | App STA 回归套件通过控件同步路径；结构由 formatter/build/analyzer 门禁复核。 |
| #39 | **Accepted** | UI TextBox 无上限会随长会话增长。上限设为 1,000,000 字符；超限从最早完整行裁到约 750,000 后追加并滚动到末尾，磁盘日志不变。 | `UiLogIsTrimmedOnWholeLineAndRemainsBounded`。 |
| #40 | **Accepted** | 关窗可能先 Dispose CTS/composition，再由在飞 async action 访问。现在首次关闭先禁止新 action、取消 lifetime 并异步等待当前 action 的取消/回滚；完成后受控再次 Close，再 Dispose controller/composition。关闭期错误只写日志，不操作已销毁 UI。 | `ClosePreparationWaitsForActiveActionAndRejectsNewActions`。 |

## 信息级条目的范围

信息级建议本轮不单独扩大修改面。由已接受修复自然覆盖的只有：统一异常显示使用 `SecretRedactor`、进程输出显式 UTF-8、以及相应诊断边界。混合换行外观、跨天日志文件名、完整堆栈策略、空 credential blob、假想跨平台路径比较、Font 生命周期和 Initial 快照文件名识别均未借本轮顺带改动。

## 自动化与现场边界

- Core Release：301 passed / 0 failed / 7 opt-in skipped。
- App Release：10 passed / 0 failed / 0 skipped；所有 WinForms 用例在隔离 STA 中运行且不显示窗口。
- Debug/Release Solution build：均为 0 warning / 0 error。
- 最终 format、diff、publish 与产物 SHA-256 记录追加在 [`VERIFICATION.md`](VERIFICATION.md)；本矩阵不改写历史验证章节。
- 真实 GUI、真实长任务和 live Provider/LM Studio 生命周期操作均保持 **Untested**。
