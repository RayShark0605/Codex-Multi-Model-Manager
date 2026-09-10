using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using CodexModelManager.Core.Abstractions;

namespace CodexModelManager.Core.Security;

/// <summary>
/// 基于 Windows 凭据管理器（Advapi32 CredWrite/CredRead/CredDelete）的密钥存取实现。
/// 凭据以 Generic 类型、本机持久化方式保存；目标名必须位于应用命名空间（CodexModelManager/ 前缀）之下。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsCredentialStore : ISecretStore
{
    private const int CredentialTypeGeneric = 1;
    private const int CredentialPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    /// <summary>保存（覆盖）凭据；密钥字节用固定 GCHandle 传入，用完立即清零并释放。</summary>
    public void Save(string targetName, ReadOnlySpan<char> secret)
    {
        ValidateTarget(targetName);
        if (secret.IsEmpty)
        {
            throw new ArgumentException("Secret cannot be empty.", nameof(secret));
        }

        var bytes = Encoding.Unicode.GetBytes(secret.ToString());
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            var credential = new NativeCredential
            {
                Type = CredentialTypeGeneric,
                TargetName = targetName,
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = handle.AddrOfPinnedObject(),
                Persist = CredentialPersistLocalMachine,
                UserName = Environment.UserName
            };
            if (!CredWrite(ref credential, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to save credential.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            handle.Free();
        }
    }

    /// <summary>读取凭据；目标不存在返回 null，Blob 为空返回空串，其余失败抛 Win32Exception。</summary>
    public string? Read(string targetName)
    {
        ValidateTarget(targetName);
        if (!CredRead(targetName, CredentialTypeGeneric, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound)
            {
                return null;
            }

            throw new Win32Exception(error, "Unable to read credential.");
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            // Blob 为 UTF-16 字节，长度除以 2 得到字符数
            return credential.CredentialBlob == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUni(credential.CredentialBlob, checked((int)credential.CredentialBlobSize / 2));
        }
        finally
        {
            CredFree(pointer);
        }
    }

    /// <summary>判断凭据是否存在。</summary>
    public bool Exists(string targetName) => Read(targetName) is not null;

    /// <summary>删除凭据；目标本就不存在视为成功，其余失败抛 Win32Exception。</summary>
    public void Delete(string targetName)
    {
        ValidateTarget(targetName);
        if (CredDelete(targetName, CredentialTypeGeneric, 0))
        {
            return;
        }

        var error = Marshal.GetLastWin32Error();
        if (error != ErrorNotFound)
        {
            throw new Win32Exception(error, "Unable to delete credential.");
        }
    }

    /// <summary>校验目标名非空且位于应用命名空间前缀之下，防止误读写其他程序的凭据。</summary>
    private static void ValidateTarget(string targetName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetName);
        if (!targetName.StartsWith(CredentialTargets.Prefix, StringComparison.Ordinal))
        {
            throw new ArgumentException("Credential target is outside the application namespace.", nameof(targetName));
        }
    }

    [DllImport("Advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite([In] ref NativeCredential userCredential, uint flags);

    [DllImport("Advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credentialPtr);

    [DllImport("Advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("Advapi32.dll", SetLastError = false)]
    private static extern void CredFree(IntPtr credential);

    /// <summary>CREDENTIAL 结构的托管映射（顺序布局须与 Win32 定义一致）。</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }
}

/// <summary>凭据目标名的统一命名空间与各 Provider 的完整目标名。</summary>
public static class CredentialTargets
{
    /// <summary>应用凭据命名空间前缀。</summary>
    public const string Prefix = "CodexModelManager/";

    /// <summary>DeepSeek 凭据完整目标名。</summary>
    public const string DeepSeek = Prefix + "DeepSeek";

    /// <summary>LM Studio 凭据完整目标名。</summary>
    public const string LmStudio = Prefix + "LMStudio";

    /// <summary>GLM 凭据完整目标名。</summary>
    public const string Glm = Prefix + "GLM";
}
