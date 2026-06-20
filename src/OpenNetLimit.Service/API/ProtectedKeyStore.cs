using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace OpenNetLimit.Service.API;

public static class ProtectedKeyStore
{
    private const string CredentialTarget = "OpenNetLimit/ApiKey";
    private const int CRED_TYPE_GENERIC = 1;
    private const int CRED_PERSIST_LOCAL_MACHINE = 2;

    private static readonly string LegacyKeyFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "OpenNetLimit", "apikey.protected");

    public static string? LoadKey()
    {
        var key = LoadFromCredentialManager();
        if (key is not null)
            return key;

        key = LoadFromDpapiLegacy();
        if (key is not null)
        {
            try
            {
                SaveToCredentialManager(key);
                File.Delete(LegacyKeyFilePath);
            }
            catch { }
        }

        return key;
    }

    public static void SaveKey(string apiKey)
    {
        SaveToCredentialManager(apiKey);
    }

    private static string? LoadFromCredentialManager()
    {
        if (!CredRead(CredentialTarget, CRED_TYPE_GENERIC, 0, out var credPtr))
            return null;

        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(credPtr);
            if (cred.CredentialBlobSize <= 0 || cred.CredentialBlob == IntPtr.Zero)
                return null;
            var bytes = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            CredFree(credPtr);
        }
    }

    private static void SaveToCredentialManager(string apiKey)
    {
        var bytes = Encoding.UTF8.GetBytes(apiKey);
        var cred = new CREDENTIAL
        {
            Type = CRED_TYPE_GENERIC,
            TargetName = CredentialTarget,
            CredentialBlobSize = bytes.Length,
            CredentialBlob = Marshal.AllocHGlobal(bytes.Length),
            Persist = CRED_PERSIST_LOCAL_MACHINE,
            UserName = "OpenNetLimit"
        };

        try
        {
            Marshal.Copy(bytes, 0, cred.CredentialBlob, bytes.Length);
            if (!CredWrite(ref cred, 0))
                throw new InvalidOperationException($"CredWrite failed: {Marshal.GetLastPInvokeError()}");
        }
        finally
        {
            Marshal.FreeHGlobal(cred.CredentialBlob);
        }
    }

    private static string? LoadFromDpapiLegacy()
    {
        if (!File.Exists(LegacyKeyFilePath))
            return null;

        try
        {
            var encrypted = File.ReadAllBytes(LegacyKeyFilePath);
            var decrypted = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.LocalMachine);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch
        {
            return null;
        }
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredWrite(ref CREDENTIAL credential, int flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }
}
