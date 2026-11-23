using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using ComIDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;
using ComIStream = System.Runtime.InteropServices.ComTypes.IStream;
using STATSTG = System.Runtime.InteropServices.ComTypes.STATSTG;

namespace Fjord.Views;

/// <summary>One entry of a drag: a folder, or a file whose content is produced on demand.</summary>
/// <param name="RelativePath">"report.pdf" or "logs\2026\app.log" — backslashes, relative to the drop folder.</param>
/// <param name="Open">Opens the content. Called only when Explorer actually copies the file (after the drop).</param>
public sealed record VirtualFile(string RelativePath, bool IsDirectory, long Size, DateTime ModifiedUtc, Func<Stream>? Open);

/// <summary>
/// Drag source data for files that don't exist locally yet ("virtual files", like attachments dragged out of
/// Outlook): Explorer reads FileGroupDescriptorW for the names, then FileContents as a stream per file — only
/// after the drop, and with <see cref="IDataObjectAsyncCapability"/> on its own background thread with its
/// own progress dialog, so neither app is blocked while files download.
/// Plain COM (no WPF DataObject), so exactly these two formats are offered to other processes.
/// </summary>
[ComVisible(true)]
public sealed class VirtualFileDataObject : ComIDataObject, IDataObjectAsyncCapability
{
    private static readonly short CfDescriptor = (short)RegisterClipboardFormat("FileGroupDescriptorW");
    private static readonly short CfContents = (short)RegisterClipboardFormat("FileContents");

    private readonly Func<IReadOnlyList<VirtualFile>> _listFiles;
    private IReadOnlyList<VirtualFile>? _files;
    private bool _asyncMode = true;
    private bool _inOperation;

    /// <summary>A file's content was requested (Explorer started copying it).</summary>
    public event Action<VirtualFile>? FileRequested;
    /// <summary>Explorer finished an asynchronous copy. Argument: true on success.</summary>
    public event Action<bool>? OperationEnded;
    /// <summary>Listing the files or opening one failed.</summary>
    public event Action<Exception>? Failed;

    /// <param name="listFiles">Builds the file list (may walk folders). Called when Explorer first asks for it.</param>
    public VirtualFileDataObject(Func<IReadOnlyList<VirtualFile>> listFiles) => _listFiles = listFiles;

    private IReadOnlyList<VirtualFile> Files => _files ??= _listFiles();

    // ───────────── IDataObject ─────────────

    public void GetData(ref FORMATETC format, out STGMEDIUM medium)
    {
        medium = default;
        try
        {
            if (format.cfFormat == CfDescriptor && (format.tymed & TYMED.TYMED_HGLOBAL) != 0)
            {
                medium.tymed = TYMED.TYMED_HGLOBAL;
                medium.unionmember = BuildDescriptor(Files);
                return;
            }
            if (format.cfFormat == CfContents && (format.tymed & TYMED.TYMED_ISTREAM) != 0 &&
                format.lindex >= 0 && format.lindex < Files.Count && !Files[format.lindex].IsDirectory)
            {
                var file = Files[format.lindex];
                FileRequested?.Invoke(file);
                var stream = new ReadOnlyComStream(file.Open!(), file);
                medium.tymed = TYMED.TYMED_ISTREAM;
                medium.unionmember = Marshal.GetComInterfaceForObject(stream, typeof(ComIStream));
                return;
            }
        }
        catch (Exception ex)
        {
            Failed?.Invoke(ex);
            throw new COMException(ex.Message, E_FAIL);
        }
        throw new COMException("Format not offered", DV_E_FORMATETC);
    }

    public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) => throw new COMException(null, E_NOTIMPL);

    public int QueryGetData(ref FORMATETC format)
    {
        if (format.cfFormat == CfDescriptor) return (format.tymed & TYMED.TYMED_HGLOBAL) != 0 ? S_OK : DV_E_TYMED;
        if (format.cfFormat == CfContents) return (format.tymed & TYMED.TYMED_ISTREAM) != 0 ? S_OK : DV_E_TYMED;
        return DV_E_FORMATETC;
    }

    public int GetCanonicalFormatEtc(ref FORMATETC formatIn, out FORMATETC formatOut)
    {
        formatOut = formatIn;
        formatOut.ptd = IntPtr.Zero;
        return DATA_S_SAMEFORMATETC;
    }

    /// <summary>The shell stores things like drag images and drop descriptions here; we don't need them.</summary>
    public void SetData(ref FORMATETC formatIn, ref STGMEDIUM medium, bool release)
    {
        if (release) ReleaseStgMedium(ref medium);
        throw new COMException(null, E_NOTIMPL);
    }

    public IEnumFORMATETC EnumFormatEtc(DATADIR direction)
    {
        if (direction != DATADIR.DATADIR_GET) throw new COMException(null, E_NOTIMPL);
        FORMATETC[] formats =
        [
            new() { cfFormat = CfDescriptor, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL },
            new() { cfFormat = CfContents, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_ISTREAM },
        ];
        Marshal.ThrowExceptionForHR(SHCreateStdEnumFmtEtc((uint)formats.Length, formats, out var enumerator));
        return enumerator;
    }

    public int DAdvise(ref FORMATETC format, ADVF advf, IAdviseSink adviseSink, out int connection)
    {
        connection = 0;
        return OLE_E_ADVISENOTSUPPORTED;
    }

    public void DUnadvise(int connection) => throw new COMException(null, OLE_E_ADVISENOTSUPPORTED);

    public int EnumDAdvise(out IEnumSTATDATA? enumAdvise)
    {
        enumAdvise = null;
        return OLE_E_ADVISENOTSUPPORTED;
    }

    // ───────────── IDataObjectAsyncCapability: lets Explorer copy on a background thread ─────────────

    public void SetAsyncMode(int doOpAsync) => _asyncMode = doOpAsync != 0;
    public void GetAsyncMode(out int isOpAsync) => isOpAsync = _asyncMode ? 1 : 0;
    public void StartOperation(IntPtr bindCtx) => _inOperation = true;
    public void InOperation(out int inAsyncOp) => inAsyncOp = _inOperation ? 1 : 0;

    public void EndOperation(int hResult, IntPtr bindCtx, uint effects)
    {
        _inOperation = false;
        OperationEnded?.Invoke(hResult >= 0);
    }

    // ───────────── FILEGROUPDESCRIPTORW ─────────────

    private const int DescriptorSize = 592;   // sizeof(FILEDESCRIPTORW)
    private const int MaxName = 259;          // MAX_PATH - 1 characters + terminator

    private const int FD_ATTRIBUTES = 0x4, FD_WRITESTIME = 0x20, FD_FILESIZE = 0x40, FD_PROGRESSUI = 0x4000;
    private const uint FD_UNICODE = 0x80000000;
    private const int FILE_ATTRIBUTE_DIRECTORY = 0x10, FILE_ATTRIBUTE_NORMAL = 0x80;

    /// <summary>
    /// Layout: UINT cItems, then per item: dwFlags(0) clsid(4) sizel(20) pointl(28) dwFileAttributes(36)
    /// ftCreationTime(40) ftLastAccessTime(48) ftLastWriteTime(56) nFileSizeHigh(64) nFileSizeLow(68) cFileName[260](72).
    /// </summary>
    private static IntPtr BuildDescriptor(IReadOnlyList<VirtualFile> files)
    {
        int bytes = 4 + files.Count * DescriptorSize;
        var hGlobal = Marshal.AllocHGlobal(bytes);   // GMEM_FIXED: the pointer is a valid HGLOBAL
        var zero = new byte[bytes];
        Marshal.Copy(zero, 0, hGlobal, bytes);
        Marshal.WriteInt32(hGlobal, 0, files.Count);

        for (int i = 0; i < files.Count; i++)
        {
            var f = files[i];
            var p = hGlobal + 4 + i * DescriptorSize;
            Marshal.WriteInt32(p, 0, unchecked((int)(FD_UNICODE | FD_ATTRIBUTES | FD_WRITESTIME | FD_PROGRESSUI | (f.IsDirectory ? 0 : FD_FILESIZE))));
            Marshal.WriteInt32(p, 36, f.IsDirectory ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL);
            Marshal.WriteInt64(p, 56, ToFileTime(f.ModifiedUtc));
            Marshal.WriteInt32(p, 64, (int)(f.Size >> 32));
            Marshal.WriteInt32(p, 68, unchecked((int)(f.Size & 0xFFFFFFFF)));
            var name = f.RelativePath.Length > MaxName ? f.RelativePath[..MaxName] : f.RelativePath;
            var chars = name.ToCharArray();
            Marshal.Copy(chars, 0, p + 72, chars.Length);   // already zero-terminated by the zero fill
        }
        return hGlobal;
    }

    private static long ToFileTime(DateTime utc)
    {
        try { return utc.ToFileTimeUtc(); } catch { return 0; }
    }

    // ───────────── IStream over a .NET stream (read-only, sequential) ─────────────

    [ComVisible(true)]
    private sealed class ReadOnlyComStream(Stream inner, VirtualFile file) : ComIStream
    {
        private bool _closed;

        public void Read(byte[] buffer, int count, IntPtr bytesRead)
        {
            int total = 0;
            if (!_closed)
            {
                // Fill the whole request: some readers treat a short read as end of file.
                while (total < count)
                {
                    int n = inner.Read(buffer, total, count - total);
                    if (n <= 0) { Close(); break; }
                    total += n;
                }
            }
            if (bytesRead != IntPtr.Zero) Marshal.WriteInt32(bytesRead, total);
        }

        public void Seek(long offset, int origin, IntPtr newPosition)
        {
            long pos;
            if (!_closed && inner.CanSeek) pos = inner.Seek(offset, (SeekOrigin)origin);
            else if (offset == 0 && origin == 1) pos = _closed ? file.Size : inner.Position;   // "where am I"
            else throw new COMException(null, STG_E_INVALIDFUNCTION);
            if (newPosition != IntPtr.Zero) Marshal.WriteInt64(newPosition, pos);
        }

        public void Stat(out STATSTG stat, int flags)
        {
            stat = new STATSTG
            {
                type = 2,   // STGTY_STREAM
                cbSize = file.Size,
                pwcsName = (flags & 1) != 0 ? null! : Path.GetFileName(file.RelativePath),   // STATFLAG_NONAME = 1
                mtime = ToFileTimeStruct(file.ModifiedUtc),
            };
        }

        private static System.Runtime.InteropServices.ComTypes.FILETIME ToFileTimeStruct(DateTime utc)
        {
            long ft = ToFileTime(utc);
            return new() { dwLowDateTime = unchecked((int)ft), dwHighDateTime = (int)(ft >> 32) };
        }

        private void Close()
        {
            if (_closed) return;
            _closed = true;
            try { inner.Dispose(); } catch { }
        }

        ~ReadOnlyComStream() => Close();   // Explorer cancelled mid-file and released us

        public void Write(byte[] pv, int cb, IntPtr pcbWritten) => throw new COMException(null, STG_E_ACCESSDENIED);
        public void SetSize(long libNewSize) => throw new COMException(null, STG_E_ACCESSDENIED);
        public void CopyTo(ComIStream pstm, long cb, IntPtr pcbRead, IntPtr pcbWritten) => throw new COMException(null, E_NOTIMPL);
        public void Commit(int grfCommitFlags) { }
        public void Revert() => throw new COMException(null, E_NOTIMPL);
        public void LockRegion(long libOffset, long cb, int dwLockType) => throw new COMException(null, STG_E_INVALIDFUNCTION);
        public void UnlockRegion(long libOffset, long cb, int dwLockType) => throw new COMException(null, STG_E_INVALIDFUNCTION);
        public void Clone(out ComIStream ppstm) => throw new COMException(null, E_NOTIMPL);
    }

    // ───────────── Win32 ─────────────

    private const int S_OK = 0, DATA_S_SAMEFORMATETC = 0x40130;
    private const int E_NOTIMPL = unchecked((int)0x80004001), E_FAIL = unchecked((int)0x80004005);
    private const int DV_E_FORMATETC = unchecked((int)0x80040064), DV_E_TYMED = unchecked((int)0x80040069);
    private const int OLE_E_ADVISENOTSUPPORTED = unchecked((int)0x80040003);
    private const int STG_E_INVALIDFUNCTION = unchecked((int)0x80030001), STG_E_ACCESSDENIED = unchecked((int)0x80030005);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormat(string format);

    [DllImport("shell32.dll")]
    private static extern int SHCreateStdEnumFmtEtc(uint count, [In] FORMATETC[] formats, out IEnumFORMATETC enumerator);

    [DllImport("ole32.dll")]
    private static extern void ReleaseStgMedium(ref STGMEDIUM medium);
}

/// <summary>Lets the drop target (Explorer) extract the files asynchronously on its own thread.</summary>
[ComImport, Guid("3D8B0590-F691-11d2-8EA9-006097DF5BD4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDataObjectAsyncCapability
{
    void SetAsyncMode(int doOpAsync);
    void GetAsyncMode(out int isOpAsync);
    void StartOperation(IntPtr bindCtx);
    void InOperation(out int inAsyncOp);
    void EndOperation(int hResult, IntPtr bindCtx, uint effects);
}

/// <summary>Starts an OLE drag directly (not through WPF), so only our COM data object is offered.</summary>
public static class OleDrag
{
    public const int DROPEFFECT_NONE = 0, DROPEFFECT_COPY = 1, DROPEFFECT_MOVE = 2;

    [ComImport, Guid("00000121-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDropSource
    {
        [PreserveSig] int QueryContinueDrag(int escapePressed, int keyState);
        [PreserveSig] int GiveFeedback(int effect);
    }

    [ComVisible(true)]
    private sealed class DropSource : IDropSource
    {
        private const int MK_LBUTTON = 0x1, DRAGDROP_S_DROP = 0x40100, DRAGDROP_S_CANCEL = 0x40101, DRAGDROP_S_USEDEFAULTCURSORS = 0x40102;

        public int QueryContinueDrag(int escapePressed, int keyState)
        {
            if (escapePressed != 0) return DRAGDROP_S_CANCEL;
            return (keyState & MK_LBUTTON) == 0 ? DRAGDROP_S_DROP : 0;
        }

        public int GiveFeedback(int effect) => DRAGDROP_S_USEDEFAULTCURSORS;
    }

    [DllImport("ole32.dll")]
    private static extern int DoDragDrop(ComIDataObject dataObject, IDropSource dropSource, int okEffects, out int effect);

    /// <summary>Runs the drag (blocks until the mouse is released). Returns the effect the target chose.</summary>
    public static int Run(ComIDataObject data, int allowedEffects)
    {
        int hr = DoDragDrop(data, new DropSource(), allowedEffects, out var effect);
        return hr == 0x40100 ? effect : DROPEFFECT_NONE;   // DRAGDROP_S_DROP
    }
}
