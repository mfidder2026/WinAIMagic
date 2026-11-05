using System.Runtime.InteropServices;

namespace WinAIMagic.Studio;

/// FileManager.trashItem: what the user removes stays recoverable.
public static class RecycleBin
{
    private const uint Delete = 3;
    private const ushort Silent = 0x4, NoConfirmation = 0x10, AllowUndo = 0x40, NoErrorUi = 0x400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FileOperation
    {
        public IntPtr Window;
        public uint Function;
        public string From;
        public string? To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool Aborted;
        public IntPtr NameMappings;
        public string? ProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHFileOperationW(ref FileOperation operation);

    public static void Move(string path)
    {
        var op = new FileOperation
        {
            Function = Delete,
            From = Path.GetFullPath(path) + "\0",     // a list of paths, ended by an empty one
            Flags = (ushort)(Silent | NoConfirmation | AllowUndo | NoErrorUi),
        };
        int code = SHFileOperationW(ref op);
        if (code != 0 || op.Aborted) throw new IOException($"{Path.GetFileName(path)}: {code}");
    }
}
