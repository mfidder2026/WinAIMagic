using System.Runtime.InteropServices;
using static WinAIMagic.Core.Support.Lang;

namespace WinAIMagic.App;

/// <summary>Windows' own open dialog through IFileOpenDialog (brief §4). The WinRT pickers run in a helper process and their
/// folder picker failed with E_FAIL when confirming from this unpackaged app.</summary>
public static class NativePickers
{
    private const uint NoChangeDir = 0x8, PickFolders = 0x20, ForceFileSystem = 0x40, AllowMultiSelect = 0x200, PathMustExist = 0x800, FileMustExist = 0x1000;
    private const uint FileSystemPath = 0x80058000;
    private const int Cancelled = unchecked((int)0x800704C7);

    /// <summary>The chosen files, empty when cancelled. Extensions as ".png".</summary>
    public static IReadOnlyList<string> Files(IntPtr owner, bool many, IReadOnlyList<string> extensions)
    {
        var dialog = (IFileOpenDialog)new FileOpenDialog();
        try
        {
            dialog.GetOptions(out var options);
            dialog.SetOptions(options | NoChangeDir | ForceFileSystem | PathMustExist | FileMustExist | (many ? AllowMultiSelect : 0));
            if (extensions.Count > 0 && extensions[0] != "*")
            {
                var spec = new[] { new FilterSpec { Name = L("Bestanden"), Spec = string.Join(";", extensions.Select(e => "*" + e)) } };
                dialog.SetFileTypes(1, spec);
            }
            if (dialog.Show(owner) == Cancelled) return [];
            dialog.GetResults(out var items);
            items.GetCount(out var count);
            var paths = new List<string>((int)count);
            for (uint i = 0; i < count; i++)
            {
                items.GetItemAt(i, out var item);
                paths.Add(PathOf(item));
            }
            return paths;
        }
        finally { Marshal.ReleaseComObject(dialog); }
    }

    /// <summary>The chosen folder, null when cancelled.</summary>
    public static string? Folder(IntPtr owner)
    {
        var dialog = (IFileOpenDialog)new FileOpenDialog();
        try
        {
            dialog.GetOptions(out var options);
            dialog.SetOptions(options | NoChangeDir | PickFolders | ForceFileSystem | PathMustExist);
            if (dialog.Show(owner) == Cancelled) return null;
            dialog.GetResult(out var item);
            return PathOf(item);
        }
        finally { Marshal.ReleaseComObject(dialog); }
    }

    private static string PathOf(IShellItem item)
    {
        item.GetDisplayName(FileSystemPath, out var p);
        try { return Marshal.PtrToStringUni(p) ?? ""; }
        finally { Marshal.FreeCoTaskMem(p); }
    }

    [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
    private class FileOpenDialog { }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FilterSpec
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string Name;
        [MarshalAs(UnmanagedType.LPWStr)] public string Spec;
    }

    [ComImport, Guid("d57c7288-d4ad-4768-be02-9d969532d960"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog
    {
        [PreserveSig] int Show(IntPtr owner);
        void SetFileTypes(uint count, [MarshalAs(UnmanagedType.LPArray)] FilterSpec[] filters);
        void SetFileTypeIndex(uint index);
        void GetFileTypeIndex(out uint index);
        void Advise(IntPtr events, out uint cookie);
        void Unadvise(uint cookie);
        void SetOptions(uint options);
        void GetOptions(out uint options);
        void SetDefaultFolder(IShellItem folder);
        void SetFolder(IShellItem folder);
        void GetFolder(out IShellItem folder);
        void GetCurrentSelection(out IShellItem item);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetFileName(out IntPtr name);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void GetResult(out IShellItem item);
        void AddPlace(IShellItem item, int place);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
        void Close(int result);
        void SetClientGuid(ref Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr filter);
        void GetResults(out IShellItemArray items);
        void GetSelectedItems(out IShellItemArray items);
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr ctx, ref Guid handler, ref Guid iid, out IntPtr result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint kind, out IntPtr name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IShellItem other, uint hint, out int order);
    }

    [ComImport, Guid("b63ea76d-1f85-456f-a19c-48159efa858b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemArray
    {
        void BindToHandler(IntPtr ctx, ref Guid handler, ref Guid iid, out IntPtr result);
        void GetPropertyStore(int flags, ref Guid iid, out IntPtr store);
        void GetPropertyDescriptionList(IntPtr key, ref Guid iid, out IntPtr list);
        void GetAttributes(int flags, uint mask, out uint attributes);
        void GetCount(out uint count);
        void GetItemAt(uint index, out IShellItem item);
        void EnumItems(out IntPtr items);
    }
}
