using System.Net;
using CodexModelManager.Core.Codex;
using CodexModelManager.Core.LmStudio;
using CodexModelManager.Core.Models;

namespace CodexModelManager.Tests;

/// <summary>LmStudioIdentityAndRetry 相关测试集。</summary>
public sealed class LmStudioIdentityAndRetryTests
{
    private static readonly Uri Endpoint = new("http://127.0.0.1:1234");

    [Theory]
    [InlineData("")]
    [InlineData("\"id\":null,")]
    [InlineData("\"id\":42,")]
    [InlineData("\"id\":\"  \",")]
    public async Task InvalidNativeInstanceIdIsNotInventedAndValidSiblingRemainsAvailable(string invalidId)
    {
        string models = $$$"""
            {"models":[{"key":"source","type":"llm","loaded_instances":[{ {{{invalidId}}} "config":{"context_length":32768}},{"id":"valid","config":{"context_length":32768}}]}]}
            """;
        int responses = 0;
        using var http = new HttpClient(new StubHttpHandler(request =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return StubHttpHandler.Json(models);
            }
            responses++;
            return StubHttpHandler.Json("{\"output\":[]}");
        }));
        var client = new LmStudioClient(Endpoint, httpClient: http);
        Assert.Equal("valid", Assert.Single(await client.DiscoverNativeModelsAsync()).Id);
        Assert.NotEmpty(client.DiscoveryDiagnostics);
        var preflight = new LmStudioSwitchPreflight(http);
        CodexInstructionHierarchyProbeResult missing = await preflight.ProbeAsync(Request("source"));
        Assert.Equal(CompatibilityFailureCodes.LmStudioLoadedInstanceMissing, missing.FailureCode);
        Assert.Equal(0, responses);
        Assert.True((await preflight.ProbeAsync(Request("valid"))).IsCompatible);
        Assert.Equal(4, responses);
    }

    [Fact]
    public async Task AllMembersOfDuplicateIdentityGroupAreUnavailableWithoutRejectingValidModel()
    {
        const string json = """
            {"models":[{"key":"source-a","type":"llm","loaded_instances":[{"id":"duplicate","config":{"context_length":32768}},{"id":"valid","config":{"context_length":32768}}]},{"key":"source-b","loaded_instances":[{"id":"duplicate","config":{"context_length":65536}}]}]}
            """;
        using var http = new HttpClient(new StubHttpHandler(_ => StubHttpHandler.Json(json)));
        var client = new LmStudioClient(Endpoint, httpClient: http);
        Assert.Equal("valid", Assert.Single(await client.DiscoverNativeModelsAsync()).Id);
        Assert.Contains(client.DiscoveryDiagnostics, message => message.Contains("重复", StringComparison.Ordinal));
        Assert.Equal(CompatibilityFailureCodes.LmStudioLoadedInstanceMissing, (await new LmStudioSwitchPreflight(http).ProbeAsync(Request("duplicate"))).FailureCode);
    }

    [Theory]
    [InlineData(false, 3, false)]
    [InlineData(true, 7, true)]
    public async Task ConfirmedTransientRetryRechecksSnapshotsAndRepeatsCompleteFourStageGroup(bool confirmed, int expectedResponses, bool expectedCompatible)
    {
        int responses = 0;
        int nativeReads = 0;
        using var http = new HttpClient(new StubHttpHandler(request =>
        {
            if (request.Method == HttpMethod.Get)
            {
                nativeReads++;
                return StubHttpHandler.Json(Models());
            }

            responses++;
            return responses == 3 ? StubHttpHandler.Json("{}", HttpStatusCode.ServiceUnavailable) : StubHttpHandler.Json("{\"output\":[]}");
        }));
        using IDisposable? scope = confirmed ? SwitchRetryBudget.BeginConfirmedSwitch() : null;
        CodexInstructionHierarchyProbeResult result = await new LmStudioSwitchPreflight(http).ProbeAsync(Request());
        Assert.Equal(expectedCompatible, result.IsCompatible);
        Assert.Equal(expectedResponses, responses);
        Assert.Equal(confirmed ? 4 : 2, nativeReads);
    }

    [Fact]
    public async Task RetryDetectsBatchConfigurationDriftWithoutSendingAnotherInference()
    {
        int batch = 512;
        int responses = 0;
        using var http = new HttpClient(new StubHttpHandler(request =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return StubHttpHandler.Json(Models(batch));
            }
            responses++;
            return StubHttpHandler.Json("{}", HttpStatusCode.ServiceUnavailable);
        }));
        using IDisposable scope = SwitchRetryBudget.BeginConfirmedSwitch(_ => { batch = 1024; return Task.CompletedTask; });
        CodexInstructionHierarchyProbeResult result = await new LmStudioSwitchPreflight(http).ProbeAsync(Request());
        Assert.Equal(CompatibilityFailureCodes.LmStudioLoadedContextChanged, result.FailureCode);
        Assert.Equal(1, responses);
    }

    [Fact]
    public async Task SecondStageCannotObtainAnotherRetryInTheSameConfirmedSwitch()
    {
        int responses = 0;
        using var http = new HttpClient(new StubHttpHandler(request =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return StubHttpHandler.Json(Models());
            }
            responses++;
            return StubHttpHandler.Json("{}", HttpStatusCode.ServiceUnavailable);
        }));
        using IDisposable scope = SwitchRetryBudget.BeginConfirmedSwitch();
        var preflight = new LmStudioSwitchPreflight(http);
        Assert.False((await preflight.ProbeAsync(Request())).IsCompatible);
        Assert.False((await preflight.ProbeAsync(Request())).IsCompatible);
        Assert.Equal(3, responses);
    }

    private static SwitchRequest Request(string id = "valid") => new(ProviderKind.LmStudio, id, ContextWindow: 32768, LmStudioEndpoint: Endpoint);

    private static string Models(int batch = 512) => $$$"""
        {"models":[{"key":"source","type":"llm","loaded_instances":[{"id":"valid","config":{"context_length":32768,"eval_batch_size":{{{batch}}}}}]}]}
        """;
}
