using CodexModelManager.Core.Security;

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
    if (string.IsNullOrEmpty(token)) return 4;
    Console.Out.Write(token);
    return 0;
}
catch
{
    // Never print exception details: credential errors may contain target metadata.
    return 5;
}
