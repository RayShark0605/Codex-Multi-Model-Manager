using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexModelManager.Core.Codex;
using CodexModelManager.Core.Infrastructure;
using CodexModelManager.Core.Models;
using CodexModelManager.Core.Providers;

namespace CodexModelManager.Tests;

/// <summary>Catalog 相关测试集。</summary>
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
    public async Task FutureDeepSeekCatalogWithoutHistoricalSlugsIsReused()
    {
        using var root = new TemporaryDirectory();
        var home = new TestCodexHomeProvider(Path.Combine(root.Path, "home"));
        string models = Path.Combine(home.Home, "models.json");
        const string json = "{\"models\":[{\"slug\":\"deepseek-v5-next\",\"context_window\":100,\"minimal_client_version\":\"0.155.0\",\"apply_patch_tool_type\":\"freeform\",\"shell_type\":\"shell_command\",\"supported_reasoning_levels\":[]}]}";
        await File.WriteAllTextAsync(models, json, new UTF8Encoding(false));
        using var http = new HttpClient(new StubHttpHandler(_ => StubHttpHandler.Json("", HttpStatusCode.InternalServerError)));
        var service = new DeepSeekCatalogService(home, new AppPaths(Path.Combine(root.Path, "local")), http);

        Assert.Equal(models, await service.EnsureDeepSeekCatalogAsync());
        Assert.Contains(await service.GetDeepSeekModelsAsync(), model => model.Id == "deepseek-v5-next");
    }

    [Fact]
    public async Task FutureDeepSeekCatalogWithoutReasoningMetadataIsReused()
    {
        using var root = new TemporaryDirectory();
        var home = new TestCodexHomeProvider(Path.Combine(root.Path, "home"));
        string models = Path.Combine(home.Home, "models.json");
        const string json = "{\"models\":[{\"slug\":\"deepseek-v5-fast\",\"context_window\":100,\"minimal_client_version\":\"0.155.0\",\"apply_patch_tool_type\":\"freeform\",\"shell_type\":\"shell_command\",\"supported_reasoning_levels\":null}]}";
        await File.WriteAllTextAsync(models, json, new UTF8Encoding(false));
        using var http = new HttpClient(new StubHttpHandler(_ => StubHttpHandler.Json("", HttpStatusCode.InternalServerError)));
        var service = new DeepSeekCatalogService(home, new AppPaths(Path.Combine(root.Path, "local")), http);

        Assert.Equal(models, await service.EnsureDeepSeekCatalogAsync());
        Assert.Contains(await service.GetDeepSeekModelsAsync(), model => model.Id == "deepseek-v5-fast" && model.ReasoningOptions is { Count: 0 });
    }

    [Fact]
    public async Task FutureGlmCatalogWithoutHistoricalSlugsIsReused()
    {
        using var root = new TemporaryDirectory();
        var home = new TestCodexHomeProvider(Path.Combine(root.Path, "home"));
        string models = Path.Combine(home.Home, "models.json");
        const string json = "{\"models\":[{\"slug\":\"glm-6-next\",\"context_window\":100,\"apply_patch_tool_type\":\"freeform\",\"shell_type\":\"shell_command\",\"supported_reasoning_levels\":[]}]}";
        byte[] catalogBytes = new UTF8Encoding(false).GetBytes(json);
        await File.WriteAllBytesAsync(models, catalogBytes);
        await File.WriteAllTextAsync(models + ".provenance.json", JsonSerializer.Serialize(new
        {
            source = GlmPlatforms.OfficialDocsUrl(GlmPlatform.BigModel),
            catalogSha256 = Convert.ToHexString(SHA256.HashData(catalogBytes)),
        }));
        using var http = new HttpClient(new StubHttpHandler(_ => StubHttpHandler.Json("", HttpStatusCode.InternalServerError)));
        var service = new GlmCatalogService(home, new AppPaths(Path.Combine(root.Path, "local")), http);

        Assert.Equal(models, await service.EnsureGlmCatalogAsync(GlmPlatform.BigModel));
        Assert.Contains(await service.GetGlmModelsAsync(GlmPlatform.BigModel), model => model.Id == "glm-6-next");
    }

    [Fact]
    public async Task FutureGlmCatalogWithoutReasoningMetadataIsReused()
    {
        using var root = new TemporaryDirectory();
        var home = new TestCodexHomeProvider(Path.Combine(root.Path, "home"));
        string models = Path.Combine(home.Home, "models.json");
        const string json = "{\"models\":[{\"slug\":\"glm-6-fast\",\"context_window\":100,\"apply_patch_tool_type\":\"freeform\",\"shell_type\":\"shell_command\",\"supported_reasoning_levels\":null}]}";
        byte[] catalogBytes = new UTF8Encoding(false).GetBytes(json);
        await File.WriteAllBytesAsync(models, catalogBytes);
        await File.WriteAllTextAsync(models + ".provenance.json", JsonSerializer.Serialize(new
        {
            source = GlmPlatforms.OfficialDocsUrl(GlmPlatform.BigModel),
            catalogSha256 = Convert.ToHexString(SHA256.HashData(catalogBytes)),
        }));
        using var http = new HttpClient(new StubHttpHandler(_ => StubHttpHandler.Json("", HttpStatusCode.InternalServerError)));
        var service = new GlmCatalogService(home, new AppPaths(Path.Combine(root.Path, "local")), http);

        Assert.Equal(models, await service.EnsureGlmCatalogAsync(GlmPlatform.BigModel));
        Assert.Contains(await service.GetGlmModelsAsync(GlmPlatform.BigModel), model => model.Id == "glm-6-fast" && model.ReasoningOptions is { Count: 0 });
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

    [Fact]
    public async Task DeepSeekCatalogPromotionLeavesNoTempFileWhenCacheMoveFails()
    {
        using var root = new TemporaryDirectory();
        var home = new TestCodexHomeProvider(Path.Combine(root.Path, "home"));
        var paths = new AppPaths(Path.Combine(root.Path, "local"));
        paths.EnsureDirectories();
        string cachePath = Path.Combine(paths.CatalogDirectory, "deepseek-models.json");
        Directory.CreateDirectory(cachePath);
        string script = "@$ModelsJson = @'\n" +
            "{\"models\":[{\"slug\":\"deepseek-v4-pro\",\"context_window\":128,\"minimal_client_version\":\"0.144.0\",\"supported_reasoning_levels\":[]}] }" +
            "\n'@";
        using var http = new HttpClient(new StubHttpHandler(_ => StubHttpHandler.Json(script, HttpStatusCode.OK)));
        var service = new DeepSeekCatalogService(home, paths, http);

        await Assert.ThrowsAsync<IOException>(() => service.EnsureDeepSeekCatalogAsync());

        Assert.Empty(Directory.EnumerateFiles(paths.CatalogDirectory, "deepseek-models.json.tmp-*"));
    }

    [Fact]
    public async Task DeepSeekCatalogPromotionLeavesNoTempFileWhenProvenanceMoveFails()
    {
        using var root = new TemporaryDirectory();
        var home = new TestCodexHomeProvider(Path.Combine(root.Path, "home"));
        var paths = new AppPaths(Path.Combine(root.Path, "local"));
        paths.EnsureDirectories();
        string provenancePath = Path.Combine(paths.CatalogDirectory, "deepseek-models.json.provenance.json");
        Directory.CreateDirectory(provenancePath);
        string script = "@$ModelsJson = @'\n" +
            "{\"models\":[{\"slug\":\"deepseek-v4-pro\",\"context_window\":128,\"minimal_client_version\":\"0.144.0\",\"supported_reasoning_levels\":[]}] }" +
            "\n'@";
        using var http = new HttpClient(new StubHttpHandler(_ => StubHttpHandler.Json(script, HttpStatusCode.OK)));
        var service = new DeepSeekCatalogService(home, paths, http);

        await Assert.ThrowsAsync<IOException>(() => service.EnsureDeepSeekCatalogAsync());

        Assert.Empty(Directory.EnumerateFiles(paths.CatalogDirectory, "deepseek-models.json.provenance.json.tmp-*"));
    }

    [Fact]
    public async Task GlmCatalogPromotionLeavesNoTempFileWhenCacheMoveFails()
    {
        using var root = new TemporaryDirectory();
        var home = new TestCodexHomeProvider(Path.Combine(root.Path, "home"));
        var paths = new AppPaths(Path.Combine(root.Path, "local"));
        paths.EnsureDirectories();
        string cachePath = Path.Combine(paths.CatalogDirectory, GlmPlatforms.CacheFileName(GlmPlatform.BigModel));
        Directory.CreateDirectory(cachePath);
        string document = "```json\n" + SwitchHarness.TestGlmCatalog.TrimEnd() + "\n```";
        using var http = new HttpClient(new StubHttpHandler(_ => StubHttpHandler.Json(document, HttpStatusCode.OK)));
        var service = new GlmCatalogService(home, paths, http);

        await Assert.ThrowsAsync<IOException>(() => service.EnsureGlmCatalogAsync(GlmPlatform.BigModel));

        Assert.Empty(Directory.EnumerateFiles(paths.CatalogDirectory, "glm-bigmodel-models.json.tmp-*"));
    }

    [Fact]
    public async Task GlmCatalogPromotionLeavesNoTempFileWhenProvenanceMoveFails()
    {
        using var root = new TemporaryDirectory();
        var home = new TestCodexHomeProvider(Path.Combine(root.Path, "home"));
        var paths = new AppPaths(Path.Combine(root.Path, "local"));
        paths.EnsureDirectories();
        string provenancePath = Path.Combine(paths.CatalogDirectory, "glm-bigmodel-models.json.provenance.json");
        Directory.CreateDirectory(provenancePath);
        string document = "```json\n" + SwitchHarness.TestGlmCatalog.TrimEnd() + "\n```";
        using var http = new HttpClient(new StubHttpHandler(_ => StubHttpHandler.Json(document, HttpStatusCode.OK)));
        var service = new GlmCatalogService(home, paths, http);

        await Assert.ThrowsAsync<IOException>(() => service.EnsureGlmCatalogAsync(GlmPlatform.BigModel));

        Assert.Empty(Directory.EnumerateFiles(paths.CatalogDirectory, "glm-bigmodel-models.json.provenance.json.tmp-*"));
    }
}
