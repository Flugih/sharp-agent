namespace SharpAgent.Core;

static class UserDataProtection
{
    [StructLayout(LayoutKind.Sequential)]
    readonly struct Blob(int size, IntPtr bytes)
    {
        internal readonly int Size = size;
        internal readonly IntPtr Bytes = bytes;
    }

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CryptProtectData(in Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CryptUnprotectData(in Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);

    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr bytes);

    const string Prefix = "dpapi:v1:";

    internal static string Protect(string text) => Prefix + Convert.ToBase64String(Transform(Encoding.UTF8.GetBytes(text), true));

    internal static string Read(string text)
    {
        if (!text.StartsWith(Prefix, StringComparison.Ordinal)) return text;
        try { return Encoding.UTF8.GetString(Transform(Convert.FromBase64String(text[Prefix.Length..]), false)); }
        catch (FormatException ex) { throw new IOException("Не удалось прочитать защищённые данные Sharp Agent.", ex); }
    }

    static byte[] Transform(byte[] bytes, bool protect)
    {
        var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        Blob output = default;
        try
        {
            var input = new Blob(bytes.Length, pin.AddrOfPinnedObject());
            bool ok = protect
                ? CryptProtectData(in input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(in input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new IOException("Windows не смогла обработать защищённые данные Sharp Agent.", new Win32Exception());
            var result = new byte[output.Size];
            Marshal.Copy(output.Bytes, result, 0, result.Length);
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            pin.Free();
            if (output.Bytes != IntPtr.Zero) LocalFree(output.Bytes);
        }
    }
}
