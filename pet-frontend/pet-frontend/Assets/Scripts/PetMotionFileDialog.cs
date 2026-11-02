using System;
using System.Runtime.InteropServices;
using System.Text;

/// <summary>Native desktop file selection; the selected source is never modified here.</summary>
public static class PetMotionFileDialog
{
    public static string PickMotion()
    {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        var dialog = new OpenFileName
        {
            owner = GetActiveWindow(),
            filter = "动作文件 (VMD)\0*.vmd\0所有文件\0*.*\0\0",
            file = new StringBuilder(32768),
            maxFile = 32768,
            title = "导入桌宠动作（保留原始文件）",
            flags = 0x00080000 | 0x00001000 | 0x00000800 | 0x00000008 // Explorer, file/path exist, no CWD change.
        };
        dialog.structSize = Marshal.SizeOf(typeof(OpenFileName));
        if (GetOpenFileName(dialog)) return dialog.file.ToString();
        int error = CommDlgExtendedError();
        if (error != 0) throw new InvalidOperationException("无法打开动作选择窗口（" + error + "）。");
        return null;
#else
        throw new PlatformNotSupportedException("动作文件选择目前仅支持 Windows 桌面版。");
#endif
    }

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class OpenFileName
    {
        public int structSize;
        public IntPtr owner;
        public IntPtr instance;
        public string filter;
        public IntPtr customFilter;
        public int maxCustomFilter;
        public int filterIndex = 1;
        public StringBuilder file;
        public int maxFile;
        public IntPtr fileTitle;
        public int maxFileTitle;
        public string initialDirectory;
        public string title;
        public int flags;
        public short fileOffset;
        public short fileExtension;
        public string defaultExtension;
        public IntPtr customData;
        public IntPtr hook;
        public string templateName;
        public IntPtr reserved;
        public int reservedValue;
        public int extendedFlags;
    }
    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetOpenFileNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetOpenFileName([In, Out] OpenFileName dialog);
    [DllImport("comdlg32.dll")] private static extern int CommDlgExtendedError();
    [DllImport("user32.dll")] private static extern IntPtr GetActiveWindow();
#endif
}

