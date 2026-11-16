using System.Runtime.InteropServices.ComTypes;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;
using IDataObject = System.Windows.IDataObject;

namespace SharpAgent.Ui;

public static class VirtualFiles
{
    const string Descriptor = "FileGroupDescriptorW";
    const int EntrySize = 592;
    const uint HasAttributes = 0x4, HasSize = 0x40, DirectoryAttribute = 0x10;
    const long MaxBytes = 200L * 1024 * 1024;

    [DllImport("ole32.dll")]
    static extern void ReleaseStgMedium(ref STGMEDIUM medium);

    [DllImport("kernel32.dll")]
    static extern nint GlobalLock(nint handle);

    [DllImport("kernel32.dll")]
    static extern bool GlobalUnlock(nint handle);

    [DllImport("kernel32.dll")]
    static extern nuint GlobalSize(nint handle);

    public static bool HasFiles(IDataObject data)
    {
        try { return Entries(data).Any(); }
        catch { return false; }
    }

    public static IEnumerable<(string Name, byte[] Data)> Read(IDataObject data)
    {
        if (data is not ComDataObject com) yield break;
        short format = unchecked((short)DataFormats.GetDataFormat("FileContents").Id);
        foreach (var (index, name, size) in Entries(data))
            if (ReadContents(com, format, index, size) is { } bytes) yield return (name, bytes);
    }

    static IEnumerable<(int Index, string Name, long? Size)> Entries(IDataObject data)
    {
        if (!data.GetDataPresent(Descriptor) || data.GetData(Descriptor) is not MemoryStream stream) yield break;
        var bytes = stream.ToArray();
        int count = bytes.Length >= 4 ? BitConverter.ToInt32(bytes, 0) : 0;
        for (int i = 0; i < count && 4 + (i + 1) * EntrySize <= bytes.Length; i++)
        {
            int start = 4 + i * EntrySize;
            uint flags = BitConverter.ToUInt32(bytes, start);
            if ((flags & HasAttributes) != 0 && (BitConverter.ToUInt32(bytes, start + 36) & DirectoryAttribute) != 0) continue;
            var name = Path.GetFileName(Encoding.Unicode.GetString(bytes, start + 72, 520).Split('\0')[0]);
            name = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
            if (name.Length == 0 || name.EndsWith(".url", StringComparison.OrdinalIgnoreCase)) continue;
            if (Regex.IsMatch(name, @"^(con|prn|aux|nul|com[0-9¹²³]|lpt[0-9¹²³]) *(\..*)?$", RegexOptions.IgnoreCase)) name = "_" + name;
            long? size = (flags & HasSize) != 0
                ? ((long)BitConverter.ToUInt32(bytes, start + 64) << 32) | BitConverter.ToUInt32(bytes, start + 68)
                : null;
            yield return (i, name, size);
        }
    }

    static byte[]? ReadContents(ComDataObject data, short format, int index, long? size)
    {
        if (size > MaxBytes) return null;
        var request = new FORMATETC
        {
            cfFormat = format, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = index, tymed = TYMED.TYMED_ISTREAM | TYMED.TYMED_HGLOBAL,
        };
        STGMEDIUM medium;
        try { data.GetData(ref request, out medium); }
        catch (COMException) { return null; }
        try
        {
            var bytes = medium.tymed switch
            {
                TYMED.TYMED_ISTREAM => ReadStream((IStream)Marshal.GetObjectForIUnknown(medium.unionmember)),
                TYMED.TYMED_HGLOBAL => ReadGlobal(medium.unionmember),
                _ => null,
            };
            return bytes != null && size < bytes.Length ? bytes[..(int)size] : bytes;
        }
        finally { ReleaseStgMedium(ref medium); }
    }

    static byte[]? ReadStream(IStream stream)
    {
        var result = new MemoryStream();
        var buffer = new byte[81920];
        var read = Marshal.AllocCoTaskMem(sizeof(int));
        try
        {
            while (true)
            {
                stream.Read(buffer, buffer.Length, read);
                int count = Marshal.ReadInt32(read);
                if (count <= 0) return result.ToArray();
                result.Write(buffer, 0, count);
                if (result.Length > MaxBytes) return null;
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(read);
            Marshal.ReleaseComObject(stream);
        }
    }

    static byte[]? ReadGlobal(nint handle)
    {
        long size = (long)GlobalSize(handle);
        if (size <= 0 || size > MaxBytes) return null;
        var pointer = GlobalLock(handle);
        if (pointer == 0) return null;
        try
        {
            var bytes = new byte[size];
            Marshal.Copy(pointer, bytes, 0, (int)size);
            return bytes;
        }
        finally { GlobalUnlock(handle); }
    }
}
