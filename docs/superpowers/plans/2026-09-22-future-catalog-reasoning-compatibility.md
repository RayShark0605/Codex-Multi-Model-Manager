# Future Catalog Reasoning Compatibility Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让未来 DeepSeek/GLM 官方目录在合法地省略或置空 reasoning metadata 时仍可被安全发现并完成配置切换，而不因 JSON 数组枚举异常误报失败。

**Architecture:** 保留严格的 catalog 根结构、slug 命名空间、上下文、版本、工具 metadata 与重复 slug 校验；仅把 reasoning metadata 视为可选能力，在来源识别、模型解析和切换语义校验中统一按“无可写 reasoning effort”处理。通过 catalog 服务回退测试与真实 `ConfigurationSwitchService` 计划测试覆盖发现到切换的完整数据流。

**Tech Stack:** .NET 8 / C#、System.Text.Json、xUnit v3、Tomlyn、现有临时 Codex Home 与 fake HTTP/process 测试基础设施。

**Spec:** `README.md` 的“四类 Provider”与 `docs/OFFICIAL-COMPATIBILITY-NOTES.md` 的 OpenAI/DeepSeek/GLM 目录兼容约束。

## Global Constraints

- 不写入真实 `CODEX_HOME`、真实凭据、真实 Provider API 或 LM Studio 生命周期。
- 保留当前工作树已有的未来 slug 修复，不回退或覆盖用户未提交改动。
- 未知/非对象/重复 slug、非法 context、非法版本与不完整工具 metadata 继续 fail closed。
- reasoning 缺失或为 `null` 只表示无可用 reasoning effort，不伪造 `low/medium/high/max`。
- C# 变量使用小驼峰、函数使用大驼峰；大括号单独成行；不引入无关重构。

## Review Focus

- 官方目录只含一个合法的 `deepseek-*`/`glm-*` 模型且没有 reasoning 数组时，目录应被复用而不是回退/误报无关来源。
- `supported_reasoning_levels: null` 时，模型解析应成功且不生成 reasoning options。
- 从该模型生成切换计划时，`model_reasoning_effort` 应保持删除/不写入，不能抛 `InvalidOperationException`。
- reasoning 数组存在但包含非法条目时，既有严格 catalog 校验仍必须拒绝。
- 未来非历史 slug 仍应沿用当前命名空间与工具 metadata 规则，不重新引入历史模型名锚点。

### Task 1: Relax optional reasoning source detection and add catalog regressions

**Files:**
- Modify: `src/CodexModelManager.Core/Providers/DeepSeekCatalogService.cs:ContainsOfficialModels`
- Modify: `src/CodexModelManager.Core/Providers/GlmCatalogService.cs:ContainsOfficialModels`
- Modify: `tests/CodexModelManager.Tests/CatalogTests.cs`

**Interfaces:**
- Consumes: Existing `ValidateCatalog` shape validation and future namespace/tool metadata rules.
- Produces: `EnsureDeepSeekCatalogAsync` / `EnsureGlmCatalogAsync` reuse of a structurally valid future catalog whose reasoning field is absent or null.

- [x] **Step 1: Write failing tests**

  Add one DeepSeek and one GLM test that writes a future namespaced catalog with `apply_patch_tool_type="freeform"`, `shell_type="shell_command"`, valid context/version, and `supported_reasoning_levels:null`, then makes HTTP return an error. Assert the service returns the existing `models.json` path and returns the model from `Get*ModelsAsync`.

- [x] **Step 2: Run the focused tests and verify RED**

  Run:

  ```powershell
  dotnet test .\tests\CodexModelManager.Tests\CodexModelManager.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~CatalogTests.Future"
  ```

  Expected: the new tests fail because `ContainsOfficialModels` currently requires `supported_reasoning_levels` to be an array.

- [x] **Step 3: Implement the minimal source change**

  In both catalog services, keep the namespace and exact tool metadata checks but remove only the `supported_reasoning_levels.ValueKind == Array` requirement from `ContainsOfficialModels`. `ValidateCatalog` remains the authority for optional reasoning shape.

- [x] **Step 4: Run the focused tests and verify GREEN**

  Re-run the command from Step 2 and confirm both new tests pass with no failures.

### Task 2: Make switch-time reasoning extraction null-safe and add end-to-end plan regression

**Files:**
- Modify: `src/CodexModelManager.Core/Codex/ConfigurationSwitchService.cs:GetReasoningLevels`
- Modify: `tests/CodexModelManager.Tests/SwitchMatrixTests.cs`

**Interfaces:**
- Consumes: Validated DeepSeek/GLM catalog entries whose optional reasoning field may be absent or null.
- Produces: A switch plan that leaves `model_reasoning_effort` unset when the selected model has no reasoning options.

- [x] **Step 1: Write the failing test**

  Add a switch-matrix test using the existing temporary harness and a future GLM catalog entry with `supported_reasoning_levels:null` and no `default_reasoning_level`. Create a GLM request without an explicit reasoning effort, call `CreatePlanAsync`, and assert the candidate config has the selected model/provider but no `model_reasoning_effort` key.

- [x] **Step 2: Run the focused test and verify RED**

  Run:

  ```powershell
  dotnet test .\tests\CodexModelManager.Tests\CodexModelManager.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~SwitchMatrixTests.FutureGlm"
  ```

  Expected: the test fails with `InvalidOperationException` from `JsonElement.EnumerateArray()` on a null/missing reasoning value.

- [x] **Step 3: Implement the minimal source change**

  Update `GetReasoningLevels` to return an empty set unless the property exists and has `JsonValueKind.Array`; retain the existing validated effort extraction for array entries.

- [x] **Step 4: Run focused and neighboring tests**

  Run:

  ```powershell
  dotnet test .\tests\CodexModelManager.Tests\CodexModelManager.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~SwitchMatrixTests|FullyQualifiedName~CatalogTests"
  ```

  Confirm the new regression and existing catalog/switch coverage pass.

- [x] **Step 5: Run the full verification gate**

  Run the Core and App Release suites, Debug/Release builds, formatter verification, and `git diff --check`; record exact counts and any explicit live skips in `docs/VERIFICATION.md`.

## Self-Review Checklist

- [x] Optional reasoning is relaxed only after the existing strict JSON validator accepts the catalog.
- [x] No historical model slug or provider endpoint is reintroduced as a future compatibility requirement.
- [x] The plan tests both discovery/reuse and switch-time behavior, including the no-reasoning configuration output.
- [x] All commands are bounded to temporary test state; no live mutation flags are enabled.

### Task 3: Isolate ambiguous GLM home catalogs by platform provenance

**Files:**
- Modify: `src/CodexModelManager.Core/Providers/GlmCatalogService.cs`
- Modify: `tests/CodexModelManager.Tests/GlmProviderTests.cs`
- Modify: `README.md`
- Modify: `docs/VERIFICATION.md`

**Root cause:** `~/.codex/models.json` has no intrinsic BigModel/Z.ai platform marker, while the two official catalogs can differ. Reusing it for either target silently crosses the endpoint/catalog boundary.

- [x] Added a RED test for unproven shared catalog reuse across platforms.
- [x] Added a matching-provenance reuse regression to preserve the safe existing path.
- [x] Added source URL and catalog SHA-256 validation before home catalog reuse.
- [x] Kept fallback to platform-specific cache/download/embedded snapshot fail-closed.
