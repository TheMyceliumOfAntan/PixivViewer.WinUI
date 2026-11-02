using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Pixiv_Viewer_WinUI.Services;

/// <summary>
/// 本地密钥存储：用 Windows DPAPI（当前用户作用域）加密后落盘。
/// 只有同一 Windows 账号能解密，拷贝到别的机器/账户无效。
/// </summary>
internal static class SecretStore
{
    private const int CryptProtectUiForbidden = 0x1;

    public static void Save(string path, string value)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var encrypted = Protect(Encoding.UTF8.GetBytes(value));
            if (encrypted is null)
            {
                return;
            }

            File.WriteAllBytes(path, encrypted);
        }
        catch (IOException)
        {
            // 忽略写盘失败
        }
    }

    public static string? Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var bytes = File.ReadAllBytes(path);
            if (bytes.Length == 0)
            {
                return null;
            }

            var plain = Unprotect(bytes);

            // 兼容旧版明文文件（升级后首次读取时顺带迁移）
            return plain is not null
                ? Encoding.UTF8.GetString(plain).Trim()
                : Encoding.UTF8.GetString(bytes).Trim();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>旧明文文件 → 加密文件，成功后删除旧文件。</summary>
    public static void MigratePlainFile(string oldPath, string newPath)
    {
        try
        {
            if (!File.Exists(oldPath) || File.Exists(newPath))
            {
                return;
            }

            var value = File.ReadAllText(oldPath).Trim();
            if (value.Length == 0)
            {
                return;
            }

            Save(newPath, value);
            File.Delete(oldPath);
        }
        catch (Exception)
        {
            // 迁移失败不影响使用
        }
    }

    private static byte[]? Protect(byte[] data)
    {
        var input = ToBlob(data);
        try
        {
            if (!CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out var output))
            {
                return null;
            }

            try
            {
                return FromBlob(output);
            }
            finally
            {
                LocalFree(output.pbData);
            }
        }
        finally
        {
            LocalFree(input.pbData);
        }
    }

    private static byte[]? Unprotect(byte[] data)
    {
        var input = ToBlob(data);
        try
        {
            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out var output))
            {
                return null;
            }

            try
            {
                return FromBlob(output);
            }
            finally
            {
                LocalFree(output.pbData);
            }
        }
        finally
        {
            LocalFree(input.pbData);
        }
    }

    private static DATA_BLOB ToBlob(byte[] data)
    {
        var blob = new DATA_BLOB
        {
            cbData = data.Length,
            pbData = Marshal.AllocHGlobal(data.Length),
        };

        Marshal.Copy(data, 0, blob.pbData, data.Length);
        return blob;
    }

    private static byte[] FromBlob(DATA_BLOB blob)
    {
        var bytes = new byte[blob.cbData];
        Marshal.Copy(blob.pbData, bytes, 0, blob.cbData);
        return bytes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref DATA_BLOB pDataIn,
        string? szDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        out DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(
        ref DATA_BLOB pDataIn,
        IntPtr ppszDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        out DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);
}
