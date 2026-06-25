using System.Runtime.InteropServices;
using System.Text;

namespace AiRateLimits.Providers.Copilot;

/// <summary>
/// Minimal Windows Credential Manager access for a generic credential (token + username).
/// The secret is stored as UTF-8 bytes in the credential blob.
/// </summary>
public static class WindowsCredential
{
    private const int CredTypeGeneric = 1;
    private const int CredPersistLocalMachine = 2;

    public sealed record Stored(string UserName, string Secret);

    public static Stored? Read(string target)
    {
        if (!CredRead(target, CredTypeGeneric, 0, out var handle))
        {
            return null;
        }

        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(handle);
            var secret = cred.CredentialBlobSize > 0 && cred.CredentialBlob != IntPtr.Zero
                ? ReadBlob(cred.CredentialBlob, cred.CredentialBlobSize)
                : string.Empty;
            return new Stored(cred.UserName ?? string.Empty, secret);
        }
        finally
        {
            CredFree(handle);
        }
    }

    public static void Write(string target, string userName, string secret)
    {
        var bytes = Encoding.UTF8.GetBytes(secret);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);

            var cred = new CREDENTIAL
            {
                Type = CredTypeGeneric,
                TargetName = target,
                CredentialBlobSize = bytes.Length,
                CredentialBlob = blob,
                Persist = CredPersistLocalMachine,
                UserName = userName
            };

            if (!CredWrite(ref cred, 0))
            {
                throw new InvalidOperationException(
                    $"CredWrite failed (Win32 error {Marshal.GetLastWin32Error()}).");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(blob);
        }
    }

    public static void Delete(string target) => CredDelete(target, CredTypeGeneric, 0);

    private static string ReadBlob(IntPtr blob, int size)
    {
        var bytes = new byte[size];
        Marshal.Copy(blob, bytes, 0, size);
        return Encoding.UTF8.GetString(bytes);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CredReadW")]
    private static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CredWriteW")]
    private static extern bool CredWrite(ref CREDENTIAL credential, int flags);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CredDeleteW")]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr cred);
}
