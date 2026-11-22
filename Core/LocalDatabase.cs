namespace SharpAgent.Core;

sealed class LocalDatabase : IDisposable
{
    const string Library = "winsqlite3.dll";
    const int Row = 100, Done = 101;
    IntPtr _db;

    internal LocalDatabase(string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        try
        {
            Check(sqlite3_open_v2(file, out _db, 6, IntPtr.Zero));
            Check(sqlite3_busy_timeout(_db, 1500));
            Execute("PRAGMA journal_mode=WAL; PRAGMA temp_store=MEMORY;");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal void Execute(string sql) => Check(sqlite3_exec(_db, sql, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero));

    internal Statement Prepare(string sql)
    {
        Check(sqlite3_prepare_v2(_db, sql, -1, out var statement, IntPtr.Zero));
        return new(this, statement);
    }

    void Check(int code)
    {
        if (code is not (0 or Row or Done)) throw new IOException($"SQLite: {code}");
    }

    public void Dispose()
    {
        if (_db != IntPtr.Zero) sqlite3_close(_db);
        _db = IntPtr.Zero;
    }

    internal sealed class Statement(LocalDatabase owner, IntPtr statement) : IDisposable
    {
        internal void Bind(int index, string text) => owner.Check(sqlite3_bind_text(statement, index, text, -1, new IntPtr(-1)));
        internal string Text(int column) => Marshal.PtrToStringUTF8(sqlite3_column_text(statement, column)) ?? "";
        internal bool Step()
        {
            int code = sqlite3_step(statement);
            owner.Check(code);
            return code == Row;
        }
        internal void Reset() => owner.Check(sqlite3_reset(statement));
        public void Dispose() => sqlite3_finalize(statement);
    }

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out IntPtr db, int flags, IntPtr vfs);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    static extern int sqlite3_close(IntPtr db);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    static extern int sqlite3_busy_timeout(IntPtr db, int milliseconds);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    static extern int sqlite3_exec(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, IntPtr callback, IntPtr arg, IntPtr error);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    static extern int sqlite3_prepare_v2(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int count, out IntPtr statement, IntPtr tail);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    static extern int sqlite3_bind_text(IntPtr statement, int index, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, int bytes, IntPtr destructor);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr sqlite3_column_text(IntPtr statement, int column);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    static extern int sqlite3_step(IntPtr statement);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    static extern int sqlite3_reset(IntPtr statement);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    static extern int sqlite3_finalize(IntPtr statement);
}
