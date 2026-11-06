using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using GameSaveSync.Core.Detection;
using Microsoft.Win32.SafeHandles;

namespace GameSaveSync.Infrastructure;

/// <summary>
/// 读取文件身份（卷序列号 + 文件 ID）。等价于 Win32 的 FILE_ID_INFO（128 位文件 ID，
/// 这里取低 64 位，够用来识别"同一路径上的文件是否被替换"）。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class Win32FileIdentityProvider : IFileIdentityProvider
{
    public static Win32FileIdentityProvider Instance { get; } = new();

    public FileIdentity TryGet(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return FileIdentity.None;
        }

        try
        {
            using var handle = File.OpenHandle(
                LongPath.Ensure(path),
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                FileOptions.None);

            if (!GetFileInformationByHandleEx(handle, FileInfoByHandleClass.FileIdInfo, out var info, InfoSize))
            {
                return FileIdentity.None;
            }

            return new FileIdentity(info.VolumeSerialNumber, info.FileIdLow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return FileIdentity.None;
        }
    }

    private const int InfoSize = 24;   // VOLUME_SERIAL_NUMBER(8) + FILE_ID(16)

    private enum FileInfoByHandleClass
    {
        FileIdInfo = 18,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfo
    {
        public ulong VolumeSerialNumber;
        public ulong FileIdLow;
        public ulong FileIdHigh;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle hFile,
        FileInfoByHandleClass fileInformationClass,
        out FileIdInfo fileInformation,
        uint dwBufferSize);
}
