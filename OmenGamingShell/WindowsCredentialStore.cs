using System.Runtime.InteropServices;

namespace OmenGamingShell;

public static class WindowsCredentialStore
{
    private const uint GenericCredential = 1;
    private const uint LocalMachinePersistence = 2;

    public static void SaveMetadataKey(string sourceId, string secret)
    {
        var bytes = System.Text.Encoding.Unicode.GetBytes(secret);
        var blob = Marshal.AllocCoTaskMem(bytes.Length + sizeof(char));
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            Marshal.WriteInt16(blob, bytes.Length, 0);
            var credential = new Credential
            {
                Type = GenericCredential,
                TargetName = TargetName(sourceId),
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = LocalMachinePersistence,
                UserName = Environment.UserName
            };
            if (!CredWrite(ref credential, 0))
                throw new InvalidOperationException("Windows Credential Manager rejected the credential.");
        }
        finally
        {
            Marshal.ZeroFreeCoTaskMemUnicode(blob);
        }
    }

    public static bool HasMetadataKey(string sourceId)
    {
        if (!CredRead(TargetName(sourceId), GenericCredential, 0, out var pointer)) return false;
        CredFree(pointer);
        return true;
    }

    public static string? ReadMetadataKey(string sourceId)
    {
        if (!CredRead(TargetName(sourceId), GenericCredential, 0, out var pointer)) return null;
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0) return null;
            return Marshal.PtrToStringUni(credential.CredentialBlob, (int)credential.CredentialBlobSize / sizeof(char));
        }
        finally
        {
            CredFree(pointer);
        }
    }

    public static void DeleteMetadataKey(string sourceId)
    {
        if (CredDelete(TargetName(sourceId), GenericCredential, 0)) return;
        const int notFound = 1168;
        if (Marshal.GetLastWin32Error() != notFound)
            throw new InvalidOperationException("Windows Credential Manager could not remove the credential.");
    }

    private static string TargetName(string sourceId) => $"OmenGamingShell/Metadata/{sourceId}";

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite([In] ref Credential credential, uint flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, uint type, uint reservedFlag, out IntPtr credentialPointer);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern void CredFree(IntPtr credentialPointer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
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
