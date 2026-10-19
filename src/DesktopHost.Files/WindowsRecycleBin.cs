using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;

namespace DesktopHost.Files;

/// <summary>
/// The Recycle Bin move through the shell's <c>IFileOperation</c> — on a single-threaded apartment of its own (the interface
/// requires one), with every dialog suppressed and a progress sink that refuses any deletion the shell would not recycle.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowsRecycleBin
{
    private const uint FofSilent = 0x0004;
    private const uint FofNoConfirmation = 0x0010;
    private const uint FofAllowUndo = 0x0040;
    private const uint FofNoConfirmMkdir = 0x0200;
    private const uint FofNoErrorUi = 0x0400;
    private const uint FofNoUi = FofSilent | FofNoConfirmation | FofNoErrorUi | FofNoConfirmMkdir;
    private const uint FofxRecycleOnDelete = 0x00080000;
    private const uint FofxEarlyFailure = 0x00100000;
    private const uint FofxAddUndoRecord = 0x20000000;

    /// <summary>The flag the shell passes to <c>PreDeleteItem</c> when the item will go to the Recycle Bin.</summary>
    internal const uint TsfDeleteRecycleIfPossible = 0x00000080;

    private static readonly Guid FileOperationClsid = new("3ad05575-8857-4850-9277-11b85bdb8e09");
    private static readonly StrategyBasedComWrappers ComWrappers = new();

    public static void Move(string fullPath)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { MoveOnThisThread(fullPath); }
            catch (Exception ex) { failure = ex; }
        })
        {
            IsBackground = true,
            Name = "DesktopHost.Files trash",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure is TrashException or FileNotFoundException ? failure : Wrap(failure);
    }

    private static void MoveOnThisThread(string fullPath)
    {
        var operation = Create<IFileOperation>(FileOperationClsid, typeof(IFileOperation).GUID);
        var item = ShellItem(fullPath);
        var sink = new RecycleOnlySink();
        operation.SetOperationFlags(FofNoUi | FofAllowUndo | FofxRecycleOnDelete | FofxAddUndoRecord | FofxEarlyFailure);
        operation.Advise(sink, out var cookie);
        try
        {
            operation.DeleteItem(item, 0);
            try
            {
                operation.PerformOperations();
            }
            catch (COMException) when (sink.RefusedPermanentDelete)
            {
                // The sink's abort surfaces as a cancelled operation — the reason is below.
            }
            operation.GetAnyOperationsAborted(out var aborted);
            if (sink.RefusedPermanentDelete)
                throw new TrashException(TrashException.NotRecyclable,
                    "The item can't go to the Recycle Bin (it may be too large, or its drive has no Recycle Bin), so it was not deleted.");
            if (aborted || File.Exists(fullPath) || Directory.Exists(fullPath))
                throw new TrashException(TrashException.Refused, "Windows did not move the item to the Recycle Bin.");
        }
        finally
        {
            operation.Unadvise(cookie);
        }
    }

    private static TrashException Wrap(Exception ex) =>
        new TrashException(TrashException.Refused, "Windows did not move the item to the Recycle Bin.", ex);

    private static IShellItem ShellItem(string path)
    {
        var hr = SHCreateItemFromParsingName(path, 0, typeof(IShellItem).GUID, out var pointer);
        if (hr < 0)
        {
            if (!File.Exists(path) && !Directory.Exists(path)) throw new FileNotFoundException("Nothing is at this path.", path);
            Marshal.ThrowExceptionForHR(hr);
        }
        return Wrapped<IShellItem>(pointer);
    }

    private static T Create<T>(Guid clsid, Guid iid) where T : class
    {
        Marshal.ThrowExceptionForHR(CoCreateInstance(clsid, 0, ClsctxAll, iid, out var pointer));
        return Wrapped<T>(pointer);
    }

    private static T Wrapped<T>(nint pointer) where T : class
    {
        try { return (T)ComWrappers.GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.None); }
        finally { Marshal.Release(pointer); }
    }

    private const uint ClsctxAll = 0x17;

    [LibraryImport("ole32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int CoCreateInstance(in Guid rclsid, nint pUnkOuter, uint dwClsContext, in Guid riid, out nint ppv);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int SHCreateItemFromParsingName(string pszPath, nint pbc, in Guid riid, out nint ppv);
}

/// <summary>
/// Refuses a deletion the shell would not recycle (<c>PreDeleteItem</c> without <see cref="WindowsRecycleBin.TsfDeleteRecycleIfPossible"/>):
/// with recycling requested and no UI, the shell otherwise deletes such an item permanently.
/// </summary>
[GeneratedComClass]
[SupportedOSPlatform("windows")]
internal sealed partial class RecycleOnlySink : IFileOperationProgressSink
{
    private const int SOk = 0;
    private const int EAbort = unchecked((int)0x80004004);

    public bool RefusedPermanentDelete { get; private set; }

    public int PreDeleteItem(uint dwFlags, nint psiItem)
    {
        if ((dwFlags & WindowsRecycleBin.TsfDeleteRecycleIfPossible) != 0) return SOk;
        RefusedPermanentDelete = true;
        return EAbort;
    }

    public int StartOperations() => SOk;
    public int FinishOperations(int hrResult) => SOk;
    public int PreRenameItem(uint dwFlags, nint psiItem, nint pszNewName) => SOk;
    public int PostRenameItem(uint dwFlags, nint psiItem, nint pszNewName, int hrRename, nint psiNewlyCreated) => SOk;
    public int PreMoveItem(uint dwFlags, nint psiItem, nint psiDestinationFolder, nint pszNewName) => SOk;
    public int PostMoveItem(uint dwFlags, nint psiItem, nint psiDestinationFolder, nint pszNewName, int hrMove, nint psiNewlyCreated) => SOk;
    public int PreCopyItem(uint dwFlags, nint psiItem, nint psiDestinationFolder, nint pszNewName) => SOk;
    public int PostCopyItem(uint dwFlags, nint psiItem, nint psiDestinationFolder, nint pszNewName, int hrCopy, nint psiNewlyCreated) => SOk;
    public int PostDeleteItem(uint dwFlags, nint psiItem, int hrDelete, nint psiNewlyCreated) => SOk;
    public int PreNewItem(uint dwFlags, nint psiDestinationFolder, nint pszNewName) => SOk;
    public int PostNewItem(uint dwFlags, nint psiDestinationFolder, nint pszNewName, nint pszTemplateName, uint dwFileAttributes, int hrNew, nint psiNewItem) => SOk;
    public int UpdateProgress(uint iWorkTotal, uint iWorkSoFar) => SOk;
    public int ResetTimer() => SOk;
    public int PauseTimer() => SOk;
    public int ResumeTimer() => SOk;
}

/// <summary><c>IFileOperation</c> (shobjidl_core.h) — every member in vtable order; the ones not called take raw pointers.</summary>
[GeneratedComInterface]
[Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8")]
internal partial interface IFileOperation
{
    void Advise(IFileOperationProgressSink pfops, out uint pdwCookie);
    void Unadvise(uint dwCookie);
    void SetOperationFlags(uint dwOperationFlags);
    void SetProgressMessage(nint pszMessage);
    void SetProgressDialog(nint popd);
    void SetProperties(nint pproparray);
    void SetOwnerWindow(nint hwndOwner);
    void ApplyPropertiesToItem(nint psiItem);
    void ApplyPropertiesToItems(nint punkItems);
    void RenameItem(nint psiItem, nint pszNewName, nint pfopsItem);
    void RenameItems(nint pUnkItems, nint pszNewName);
    void MoveItem(nint psiItem, nint psiDestinationFolder, nint pszNewName, nint pfopsItem);
    void MoveItems(nint punkItems, nint psiDestinationFolder);
    void CopyItem(nint psiItem, nint psiDestinationFolder, nint pszCopyName, nint pfopsItem);
    void CopyItems(nint punkItems, nint psiDestinationFolder);
    void DeleteItem(IShellItem psiItem, nint pfopsItem);
    void DeleteItems(nint punkItems);
    void NewItem(nint psiDestinationFolder, uint dwFileAttributes, nint pszName, nint pszTemplateName, nint pfopsItem);
    void PerformOperations();
    void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool pfAnyOperationsAborted);
}

/// <summary><c>IShellItem</c> — passed, never called here; declared for its identity and vtable.</summary>
[GeneratedComInterface]
[Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
internal partial interface IShellItem
{
    void BindToHandler(nint pbc, in Guid bhid, in Guid riid, out nint ppv);
    void GetParent(out nint ppsi);
    void GetDisplayName(uint sigdnName, out nint ppszName);
    void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
    void Compare(nint psi, uint hint, out int piOrder);
}

/// <summary><c>IFileOperationProgressSink</c> — every member returns its HRESULT itself.</summary>
[GeneratedComInterface]
[Guid("04b0f1a7-9490-44bc-96e1-4296a31252e2")]
internal partial interface IFileOperationProgressSink
{
    [PreserveSig] int StartOperations();
    [PreserveSig] int FinishOperations(int hrResult);
    [PreserveSig] int PreRenameItem(uint dwFlags, nint psiItem, nint pszNewName);
    [PreserveSig] int PostRenameItem(uint dwFlags, nint psiItem, nint pszNewName, int hrRename, nint psiNewlyCreated);
    [PreserveSig] int PreMoveItem(uint dwFlags, nint psiItem, nint psiDestinationFolder, nint pszNewName);
    [PreserveSig] int PostMoveItem(uint dwFlags, nint psiItem, nint psiDestinationFolder, nint pszNewName, int hrMove, nint psiNewlyCreated);
    [PreserveSig] int PreCopyItem(uint dwFlags, nint psiItem, nint psiDestinationFolder, nint pszNewName);
    [PreserveSig] int PostCopyItem(uint dwFlags, nint psiItem, nint psiDestinationFolder, nint pszNewName, int hrCopy, nint psiNewlyCreated);
    [PreserveSig] int PreDeleteItem(uint dwFlags, nint psiItem);
    [PreserveSig] int PostDeleteItem(uint dwFlags, nint psiItem, int hrDelete, nint psiNewlyCreated);
    [PreserveSig] int PreNewItem(uint dwFlags, nint psiDestinationFolder, nint pszNewName);
    [PreserveSig] int PostNewItem(uint dwFlags, nint psiDestinationFolder, nint pszNewName, nint pszTemplateName, uint dwFileAttributes, int hrNew, nint psiNewItem);
    [PreserveSig] int UpdateProgress(uint iWorkTotal, uint iWorkSoFar);
    [PreserveSig] int ResetTimer();
    [PreserveSig] int PauseTimer();
    [PreserveSig] int ResumeTimer();
}
