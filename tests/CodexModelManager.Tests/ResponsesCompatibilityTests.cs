using System.Net;
using CodexModelManager.Core.Models;
using CodexModelManager.Core.Providers;

namespace CodexModelManager.Tests;

public sealed class ResponsesCompatibilityTests
{
    [Fact]
    public async Task LevelTwoRequiresRealFunctionCallAndDetectsStructuredReasoning()
    {
        int request = 0;
        using var http = new HttpClient(new StubHttpHandler(_ => ++request switch
        {
            1 => StubHttpHandler.Json("{\"output\":[{\"type\":\"message\"}]}"),
            2 => StubHttpHandler.Json("{\"output\":[{\"type\":\"message\"}]}"),
            3 => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("event: response.completed\ndata: {}\n") },
            4 => StubHttpHandler.Json("{\"output\":[{\"type\":\"function_call\",\"name\":\"cmm_echo\",\"arguments\":\"{\\\"value\\\":\\\"CMM_TOOL_OK\\\"}\"}]}"),
            5 => StubHttpHandler.Json("{\"output\":[{\"type\":\"reasoning\",\"summary\":[]}]}"),
            _ => throw new InvalidOperationException("Unexpected request."),
        }));
        var client = new ResponsesCompatibilityClient(http, new Uri("http://127.0.0.1:1234/"));

        CompatibilityReport report = await client.TestAsync(ProviderKind.LmStudio, "fixture", true);

        Assert.Equal(5, request);
        Assert.Equal(CompatibilityStatus.Supported, report.Results.Single(item => item.Capability == "Responses").Status);
        Assert.Equal(CompatibilityStatus.Supported, report.Results.Single(item => item.Capability == "Codex Instruction Hierarchy").Status);
        Assert.Equal(CompatibilityStatus.Supported, report.Results.Single(item => item.Capability == "Streaming").Status);
        Assert.Equal(CompatibilityStatus.Supported, report.Results.Single(item => item.Capability == "Tool Calling").Status);
        Assert.Equal(CompatibilityStatus.Supported, report.Results.Single(item => item.Capability == "Reasoning").Status);
        Assert.Equal(CompatibilityStatus.KnownLimitation, report.Results.Single(item => item.Capability == "MCP").Status);
    }

    [Fact]
    public async Task ToolCallingDoesNotPassForPlainTextImitation()
    {
        int request = 0;
        using var http = new HttpClient(new StubHttpHandler(_ => ++request switch
        {
            1 => StubHttpHandler.Json("{\"output\":[{\"type\":\"message\"}]}"),
            2 => StubHttpHandler.Json("{\"output\":[{\"type\":\"message\"}]}"),
            3 => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("data: {}\n") },
            4 => StubHttpHandler.Json("{\"output\":[{\"type\":\"message\",\"text\":\"cmm_echo CMM_TOOL_OK\"}]}"),
            _ => throw new InvalidOperationException("Unexpected request."),
        }));
        var client = new ResponsesCompatibilityClient(http, new Uri("http://127.0.0.1:1234/"));

        CompatibilityReport report = await client.TestAsync(ProviderKind.LmStudio, "fixture", false);

        Assert.Equal(CompatibilityStatus.Failed, report.Results.Single(item => item.Capability == "Tool Calling").Status);
        Assert.Equal(CompatibilityStatus.Untested, report.Results.Single(item => item.Capability == "Reasoning").Status);
        Assert.Equal(4, request);
    }

    [Fact]
    public async Task SystemOrderFailureIsClassifiedAndStopsDependentRequests()
    {
        int request = 0;
        using var http = new HttpClient(new StubHttpHandler(_ => ++request switch
        {
            1 => StubHttpHandler.Json("{\"output\":[{\"type\":\"message\"}]}"),
            2 => StubHttpHandler.Json("{\"error\":{\"message\":\"Jinja Exception: System message must be at the beginning.\",\"type\":\"server_error\"}}", HttpStatusCode.InternalServerError),
            _ => throw new InvalidOperationException("Dependent compatibility requests must not be sent."),
        }));
        var client = new ResponsesCompatibilityClient(http, new Uri("http://127.0.0.1:1234/"));

        CompatibilityReport report = await client.TestAsync(ProviderKind.LmStudio, "fixture", true);

        Assert.Equal(2, request);
        CompatibilityResult hierarchy = report.Results.Single(item => item.Capability == "Codex Instruction Hierarchy");
        Assert.Equal(CompatibilityStatus.Failed, hierarchy.Status);
        Assert.Equal(CompatibilityFailureCodes.LmStudioChatTemplateSystemOrder, hierarchy.FailureCode);
        Assert.Equal(CompatibilityStatus.Failed, report.Results.Single(item => item.Capability == "Codex Agent").Status);
        Assert.Equal(CompatibilityStatus.Untested, report.Results.Single(item => item.Capability == "Streaming").Status);
        Assert.DoesNotContain(report.Results, item => item.Detail.Contains("Jinja Exception", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DeveloperRoleFailureIsClassified()
    {
        int request = 0;
        using var http = new HttpClient(new StubHttpHandler(_ => ++request == 1
            ? StubHttpHandler.Json("{\"output\":[]}")
            : StubHttpHandler.Json("Unexpected message role: developer", HttpStatusCode.InternalServerError)));
        var probe = new CodexInstructionHierarchyProbe(http, new Uri("http://127.0.0.1:1234/"));

        CodexInstructionHierarchyProbeResult result = await probe.ProbeAsync("fixture");

        Assert.True(result.ControlPassed);
        Assert.False(result.HierarchyPassed);
        Assert.Equal(CompatibilityFailureCodes.LmStudioChatTemplateDeveloperRole, result.FailureCode);
    }

    [Fact]
    public async Task UnauthorizedControlIsClassifiedWithoutSendingDeveloperRequest()
    {
        int request = 0;
        using var http = new HttpClient(new StubHttpHandler(_ =>
        {
            request++;
            return StubHttpHandler.Json("{\"error\":{\"message\":\"secret server text\"}}", HttpStatusCode.Unauthorized);
        }));
        var probe = new CodexInstructionHierarchyProbe(http, new Uri("http://127.0.0.1:1234/"));

        CodexInstructionHierarchyProbeResult result = await probe.ProbeAsync("fixture");

        Assert.Equal(1, request);
        Assert.Equal(CompatibilityFailureCodes.AuthenticationRequired, result.FailureCode);
        Assert.DoesNotContain("secret server text", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TemplateLikeTextInFailedControlRemainsControlFailure()
    {
        using var http = new HttpClient(new StubHttpHandler(_ => StubHttpHandler.Json(
            "{\"error\":{\"message\":\"System message must be at the beginning; Unexpected message role\"}}",
            HttpStatusCode.InternalServerError)));
        var probe = new CodexInstructionHierarchyProbe(http, new Uri("http://127.0.0.1:1234/"));

        CodexInstructionHierarchyProbeResult result = await probe.ProbeAsync("fixture");

        Assert.False(result.ControlPassed);
        Assert.Equal(CompatibilityFailureCodes.ResponsesControlFailed, result.FailureCode);
    }

    [Fact]
    public async Task TimeoutAndNonJsonErrorsUseStableSafeClassifications()
    {
        using var timeoutHttp = new HttpClient(new StubHttpHandler(_ => throw new TaskCanceledException("provider body must not escape")));
        var timeoutProbe = new CodexInstructionHierarchyProbe(timeoutHttp, new Uri("http://127.0.0.1:1234/"));
        CodexInstructionHierarchyProbeResult timeout = await timeoutProbe.ProbeAsync("fixture");
        Assert.Equal(CompatibilityFailureCodes.Timeout, timeout.FailureCode);
        Assert.DoesNotContain("provider body", timeout.Detail, StringComparison.Ordinal);

        int request = 0;
        using var errorHttp = new HttpClient(new StubHttpHandler(_ => ++request == 1
            ? StubHttpHandler.Json("{\"output\":[]}")
            : new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("opaque <bearer-secret> failure") }));
        var errorProbe = new CodexInstructionHierarchyProbe(errorHttp, new Uri("http://127.0.0.1:1234/"));
        CodexInstructionHierarchyProbeResult error = await errorProbe.ProbeAsync("fixture");
        Assert.Equal(CompatibilityFailureCodes.OtherProviderError, error.FailureCode);
        Assert.DoesNotContain("bearer-secret", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProbePayloadChangesOnlyByAddingDeveloperMessage()
    {
        List<string> bodies = [];
        using var http = new HttpClient(new StubHttpHandler(request =>
        {
            bodies.Add(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return StubHttpHandler.Json("{\"output\":[]}");
        }));
        var probe = new CodexInstructionHierarchyProbe(http, new Uri("http://127.0.0.1:1234/"));

        CodexInstructionHierarchyProbeResult result = await probe.ProbeAsync("fixture");

        Assert.True(result.IsCompatible);
        Assert.Equal(2, bodies.Count);
        Assert.DoesNotContain("\"role\":\"developer\"", bodies[0], StringComparison.Ordinal);
        Assert.Contains("\"role\":\"developer\"", bodies[1], StringComparison.Ordinal);
        Assert.Contains("\"instructions\":", bodies[0], StringComparison.Ordinal);
        Assert.Contains("\"instructions\":", bodies[1], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{not-json")]
    [InlineData("{\"output\":null}")]
    public async Task SuccessfulControlWithoutOutputArrayIsRejected(string responseBody)
    {
        int request = 0;
        using var http = new HttpClient(new StubHttpHandler(_ =>
        {
            request++;
            return StubHttpHandler.Json(responseBody);
        }));
        var probe = new CodexInstructionHierarchyProbe(http, new Uri("http://127.0.0.1:1234/"));

        CodexInstructionHierarchyProbeResult result = await probe.ProbeAsync("fixture");

        Assert.Equal(1, request);
        Assert.False(result.ControlPassed);
        Assert.Equal(CompatibilityFailureCodes.ResponsesControlFailed, result.FailureCode);
        Assert.DoesNotContain(responseBody, result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SuccessfulHierarchyWithoutOutputArrayDoesNotPassGate()
    {
        int request = 0;
        using var http = new HttpClient(new StubHttpHandler(_ => ++request == 1
            ? StubHttpHandler.Json("{\"output\":[]}")
            : StubHttpHandler.Json("{}")));
        var probe = new CodexInstructionHierarchyProbe(http, new Uri("http://127.0.0.1:1234/"));

        CodexInstructionHierarchyProbeResult result = await probe.ProbeAsync("fixture");

        Assert.Equal(2, request);
        Assert.True(result.ControlPassed);
        Assert.False(result.HierarchyPassed);
        Assert.Equal(CompatibilityFailureCodes.OtherProviderError, result.FailureCode);
    }

    [Theory]
    [InlineData("file:///C:/temp/lmstudio/")]
    [InlineData("ftp://localhost:1234/")]
    [InlineData("http://user:password@localhost:1234/")]
    [InlineData("http://localhost:1234/?token=secret")]
    [InlineData("http://localhost:1234/#fragment")]
    public void UnsafeEndpointIsRejectedBeforeTokenProviderRuns(string endpoint)
    {
        int tokenReads = 0;
        using var http = new HttpClient(new StubHttpHandler(_ => throw new InvalidOperationException("HTTP must not run.")));

        Assert.Throws<InvalidOperationException>(() => new CodexInstructionHierarchyProbe(
            http,
            new Uri(endpoint),
            () =>
            {
                tokenReads++;
                return "secret";
            }));
        Assert.Equal(0, tokenReads);
    }

    [Fact]
    public void AbsoluteResponsesPathIsRejectedBeforeTokenProviderRuns()
    {
        int tokenReads = 0;
        using var http = new HttpClient(new StubHttpHandler(_ => throw new InvalidOperationException("HTTP must not run.")));

        Assert.Throws<ArgumentException>(() => new CodexInstructionHierarchyProbe(
            http,
            new Uri("http://127.0.0.1:1234/"),
            () =>
            {
                tokenReads++;
                return "secret";
            },
            "https://example.invalid/v1/responses"));
        Assert.Equal(0, tokenReads);
    }
}
