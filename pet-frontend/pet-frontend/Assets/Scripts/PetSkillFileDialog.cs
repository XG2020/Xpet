using System;
using System.Runtime.InteropServices;
using System.Text;

/// <summary>Selects a local Markdown skill without modifying the selected file.</summary>
public static class PetSkillFileDialog
{
    public static string PickSkill()
    {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        var dialog = new OpenFileName { owner = GetActiveWindow(), filter = "Markdown 技能文件\0*.md\0所有文件\0*.*\0\0",
            file = new StringBuilder(32768), maxFile = 32768, title = "导入技能书 · 选择 SKILL.md 或 Markdown 文件",
            flags = 0x00080000 | 0x00001000 | 0x00000800 | 0x00000008 };
        dialog.structSize = Marshal.SizeOf(typeof(OpenFileName));
        if (GetOpenFileName(dialog)) return dialog.file.ToString();
        int error = CommDlgExtendedError();
        if (error != 0) throw new InvalidOperationException("无法打开技能文件选择窗口（" + error + "）。");
        return null;
#else
        throw new PlatformNotSupportedException("技能文件选择目前支持 Windows 桌面版。");
#endif
    }
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private sealed class OpenFileName
    {
        public int structSize; public IntPtr owner, instance; public string filter; public IntPtr customFilter;
        public int maxCustomFilter, filterIndex = 1; public StringBuilder file; public int maxFile; public IntPtr fileTitle;
        public int maxFileTitle; public string initialDirectory, title; public int flags; public short fileOffset, fileExtension;
        public string defaultExtension; public IntPtr customData, hook; public string templateName; public IntPtr reserved;
        public int reservedValue, extendedFlags;
    }
    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetOpenFileNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetOpenFileName([In, Out] OpenFileName dialog);
    [DllImport("comdlg32.dll")] private static extern int CommDlgExtendedError();
    [DllImport("user32.dll")] private static extern IntPtr GetActiveWindow();
#endif
}
