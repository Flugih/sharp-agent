using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Windows;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;
using IDataObject = System.Windows.IDataObject;

namespace SharpAgent.Ui;

public static class VirtualFiles
{
    private const string Descriptor = "FileGroupDescriptorW";
    private const int EntrySize = 592, NameOffset = 72, NameLength = 520;
    private const uint HasAttributes = 0x4, HasSize = 0x40, DirectoryAttribute = 0x10;
    private const long MaxBytes = 200L * 1024 * 1024;

    private sealed record Entry(int Index, string Name, long? Size);

    [DllImport("ole32.dll")]
    private static extern void ReleaseStgMedium(ref STGMEDIUM medium);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern UIntPtr GlobalSize(IntPtr handle);

    public static bool HasFiles(IDataObject data)
    {
        try
        {
            return Entries(data).Any();
        }
        catch
        {
            return false;
        }
    }

    public static IEnumerable<(string Name, byte[] Data)> Read(IDataObject data)
    {
        if (data is not ComDataObject com) yield break;
        short format = unchecked((short)DataFormats.GetDataFormat("FileContents").Id);
        foreach (var entry in Entries(data))
        {
            var bytes = ReadContents(com, format, entry);
            if (bytes != null) yield return (entry.Name, bytes);
        }
    }

    private static IEnumerable<Entry> Entries(IDataObject data)
    {
        if (!data.GetDataPresent(Descriptor) || data.GetData(Descriptor) is not MemoryStream stream) yield break;
        var bytes = stream.ToArray();
        int count = bytes.Length >= 4 ? BitConverter.ToInt32(bytes, 0) : 0;
        for (int i = 0; i < count && 4 + (i + 1) * EntrySize <= bytes.Length; i++)
        {
            int start = 4 + i * EntrySize;
            uint flags = BitConverter.ToUInt32(bytes, start);
            uint attributes = BitConverter.ToUInt32(bytes, start + 36);
            if ((flags & HasAttributes) != 0 && (attributes & DirectoryAttribute) != 0) continue;

            var name = Encoding.Unicode.GetString(bytes, start + NameOffset, NameLength);
            int end = name.IndexOf('\0');
            name = Path.GetFileName((end >= 0 ? name[..end] : name).Replace('/', '\\'));
            foreach (var invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
            if (name.Length == 0 || name.EndsWith(".url", StringComparison.OrdinalIgnoreCase)) continue;

            long? size = (flags & HasSize) != 0
                ? ((long)BitConverter.ToUInt32(bytes, start + 64) << 32) | BitConverter.ToUInt32(bytes, start + 68)
                : null;
            yield return new Entry(i, name, size);
        }
    }

    private static byte[]? ReadContents(ComDataObject data, short format, Entry entry)
    {
        if (entry.Size > MaxBytes) return null;
        var request = new FORMATETC
        {
            cfFormat = format,
            dwAspect = DVASPECT.DVASPECT_CONTENT,
            lindex = entry.Index,
            tymed = TYMED.TYMED_ISTREAM | TYMED.TYMED_HGLOBAL,
        };
        STGMEDIUM medium;
        try
        {
            data.GetData(ref request, out medium);
        }
        catch (COMException)
        {
            return null;
        }
        try
        {
            var bytes = medium.tymed switch
            {
                TYMED.TYMED_ISTREAM => ReadStream((IStream)Marshal.GetObjectForIUnknown(medium.unionmember)),
                TYMED.TYMED_HGLOBAL => ReadGlobal(medium.unionmember),
                _ => null,
            };
            return bytes != null && entry.Size is { } size && size < bytes.Length ? bytes[..(int)size] : bytes;
        }
        finally
        {
            ReleaseStgMedium(ref medium);
        }
    }

    private static byte[]? ReadStream(IStream stream)
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

    private static byte[]? ReadGlobal(IntPtr handle)
    {
        long size = (long)GlobalSize(handle);
        if (size <= 0 || size > MaxBytes) return null;
        var pointer = GlobalLock(handle);
        if (pointer == IntPtr.Zero) return null;
        try
        {
            var bytes = new byte[size];
            Marshal.Copy(pointer, bytes, 0, (int)size);
            return bytes;
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }
}
