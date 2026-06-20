using System.Security.Cryptography;
using System.Text;

namespace OpenNetLimit.Service.API;

public static class ProtectedKeyStore
{
    private static readonly string KeyFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "OpenNetLimit", "apikey.protected");

    public static string? LoadKey()
    {
        if (!File.Exists(KeyFilePath))
            return null;

        try
        {
            var encrypted = File.ReadAllBytes(KeyFilePath);
            var decrypted = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.LocalMachine);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch
        {
            return null;
        }
    }

    public static void SaveKey(string apiKey)
    {
        var dir = Path.GetDirectoryName(KeyFilePath);
        if (dir is not null)
            Directory.CreateDirectory(dir);

        var plaintext = Encoding.UTF8.GetBytes(apiKey);
        var encrypted = ProtectedData.Protect(plaintext, null, DataProtectionScope.LocalMachine);

        var tempPath = KeyFilePath + ".tmp";
        File.WriteAllBytes(tempPath, encrypted);
        File.Move(tempPath, KeyFilePath, overwrite: true);
    }
}
