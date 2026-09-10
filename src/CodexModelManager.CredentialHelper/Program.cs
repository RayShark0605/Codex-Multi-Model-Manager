using CodexModelManager.Core.Security;

// 凭据助手：Codex 以 [model_providers.<id>.auth] command 方式调用，
// 从 Windows 凭据管理器按目标名读取 Token 并写到标准输出。
// 退出码：0 成功；2 参数不是单个应用命名空间目标名；3 非 Windows；4 凭据不存在；5 读取异常。

if (!OperatingSystem.IsWindows())
{
    return 3;
}

if (args.Length != 1 || !args[0].StartsWith("CodexModelManager/", StringComparison.Ordinal))
{
    return 2;
}

try
{
    var store = new WindowsCredentialStore();
    string? token = store.Read(args[0]);
    if (string.IsNullOrEmpty(token))
    {
        return 4;
    }

    Console.Out.Write(token);
    return 0;
}
catch
{
    // 绝不打印异常详情：凭据相关错误可能泄露目标名等元数据
    return 5;
}
