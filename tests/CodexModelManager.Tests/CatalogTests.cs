using System.Net;
using System.Text;
using System.Text.Json;
using CodexModelManager.Core.Codex;
using CodexModelManager.Core.Infrastructure;
using CodexModelManager.Core.Models;
using CodexModelManager.Core.Providers;

namespace CodexModelManager.Tests;

public sealed class CatalogTests
{
    [Fact]
    public async Task CorruptExistingModelsJsonIsNotOverwrittenAndFallsBackToEmbeddedSnapshot()
    {
        using var root = new TemporaryDirectory();
        var home = new TestCodexHomeProvider(Path.Combine(root.Path, "home"));
        string models = Path.Combine(home.Home, "models.json");
        await File.WriteAllTextAsync(models, "{broken");
        using var http = new HttpClient(new StubHttpHandler(_ => throw new HttpRequestException("offline")));
        var service = new DeepSeekCatalogService(home, new AppPaths(Path.Combine(root.Path, "local")), http);
        string path = await service.EnsureDeepSeekCatalogAsync();
        Assert.NotEqual(models, path);
        Assert.Equal("{broken", await File.ReadAllTextAsync(models));
        string provenance = path + ".provenance.json";
        Assert.True(File.Exists(provenance));
        using (JsonDocument provenanceJson = JsonDocument.Parse(await File.ReadAllBytesAsync(provenance)))
        {
            Assert.Equal(DeepSeekCatalogService.OfficialScriptUrl, provenanceJson.RootElement.GetProperty("source").GetString());
            Assert.False(string.IsNullOrWhiteSpace(provenanceJson.RootElement.GetProperty("scriptSha256").GetString()));
        }
        IReadOnlyList<ModelProfile> profiles = await service.GetDeepSeekModelsAsync();
        Assert.Contains(profiles, model => model.Id == "deepseek-v4-pro" && model.MinimalClientVersion == "0.144.0");
    }

    [Fact]
    public async Task ExistingOfficialCatalogIsReusedWithoutRewrite()
    {
        using var root = new TemporaryDirectory();
        var home = new TestCodexHomeProvider(Path.Combine(root.Path, "home"));
        string models = Path.Combine(home.Home, "models.json");
        const string json = "{\"models\":[{\"slug\":\"deepseek-v4-flash\",\"context_window\":100,\"minimal_client_version\":\"0.144.0\",\"apply_patch_tool_type\":\"freeform\",\"shell_type\":\"shell_command\",\"supported_reasoning_levels\":[]},{\"slug\":\"deepseek-v4-pro\",\"context_window\":100,\"minimal_client_version\":\"0.144.0\",\"apply_patch_tool_type\":\"freeform\",\"shell_type\":\"shell_command\",\"supported_reasoning_levels\":[]}]}";
        await File.WriteAllTextAsync(models, json, new UTF8Encoding(false));
        DateTime before = File.GetLastWriteTimeUtc(models);
        var service = new DeepSeekCatalogService(home, new AppPaths(Path.Combine(root.Path, "local")), new HttpClient(new StubHttpHandler(_ => StubHttpHandler.Json("", HttpStatusCode.InternalServerError))));
        Assert.Equal(models, await service.EnsureDeepSeekCatalogAsync());
        Assert.Equal(before, File.GetLastWriteTimeUtc(models));
    }

    [Fact]
    public void CatalogMissingMinimumVersionIsRejected()
    {
        Assert.Throws<InvalidDataException>(() => DeepSeekCatalogService.ValidateCatalog(Encoding.UTF8.GetBytes("{\"models\":[{\"slug\":\"x\",\"context_window\":1}]}")));
    }

    [Fact]
    public void AppServerProviderCapabilitiesAreParsedWithoutGuessing()
    {
        using JsonDocument json = JsonDocument.Parse("{\"namespaceTools\":true,\"imageGeneration\":false,\"webSearch\":true}");
        ProviderCapabilitySnapshot parsed = CodexAppServerClient.ParseProviderCapabilities(json.RootElement);
        Assert.True(parsed.NamespaceTools);
        Assert.False(parsed.ImageGeneration);
        Assert.True(parsed.WebSearch);
    }
}
