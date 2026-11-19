using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

#pragma warning disable 0649, 0169 // native structs: some fields are only read by Windows

namespace RobotVacuum.LevelEditor
{
    /// <summary>
    /// Native "choose a folder" and "choose a file" windows, so a path never has to be typed (C.1, C.7).
    /// In the Unity editor these are the editor's own panels. In a Windows build they are the Windows Explorer-style
    /// dialog (the Common Item Dialog, IFileOpenDialog); if that can't be created, the classic Windows dialogs are used.
    /// They are modal: the program waits until the window closes. Returns null when cancelled or unavailable.
    /// </summary>
    public static class FileDialogs
    {
        /// <summary>False on platforms with no dialog here (the caller keeps the text field for typing a path).</summary>
        public static bool Available =>
            Application.isEditor || Application.platform == RuntimePlatform.WindowsPlayer;

        public static string PickFolder(string title, string startFolder)
        {
#if UNITY_EDITOR
            string picked = UnityEditor.EditorUtility.OpenFolderPanel(title, ExistingFolder(startFolder) ?? "", "");
            return string.IsNullOrEmpty(picked) ? null : Path.GetFullPath(picked);
#else
            if (Application.platform != RuntimePlatform.WindowsPlayer) return null;
            try { return Modern(title, startFolder, pickFolders: true, null, null); }
            catch (Exception exception)
            {
                Debug.LogWarning($"File dialog: the Explorer-style dialog failed ({exception.Message}); using the classic one.");
                return ClassicFolder(title);
            }
#endif
        }

        /// <param name="filterName">e.g. "Programs"</param>
        /// <param name="pattern">e.g. "*.exe"; null = all files</param>
        public static string PickFile(string title, string startFolder, string filterName, string pattern)
        {
#if UNITY_EDITOR
            string extension = string.IsNullOrEmpty(pattern) ? "" : pattern.TrimStart('*', '.');
            string picked = UnityEditor.EditorUtility.OpenFilePanel(title, ExistingFolder(startFolder) ?? "", extension);
            return string.IsNullOrEmpty(picked) ? null : Path.GetFullPath(picked);
#else
            if (Application.platform != RuntimePlatform.WindowsPlayer) return null;
            try { return Modern(title, startFolder, pickFolders: false, filterName, pattern); }
            catch (Exception exception)
            {
                Debug.LogWarning($"File dialog: the Explorer-style dialog failed ({exception.Message}); using the classic one.");
                return ClassicFile(title, startFolder, filterName, pattern);
            }
#endif
        }

        /// <summary>The nearest existing folder of <paramref name="path"/> (a file's folder, or a parent), or null.</summary>
        public static string ExistingFolder(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path)) return null;
                string full = Path.GetFullPath(path.Trim().Trim('"'));
                while (!string.IsNullOrEmpty(full) && !Directory.Exists(full)) full = Path.GetDirectoryName(full);
                return string.IsNullOrEmpty(full) ? null : full;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ---------------------------------------------------------------- Windows: the Explorer-style dialog

        const uint FosNoChangeDir = 0x8, FosPickFolders = 0x20, FosForceFileSystem = 0x40, FosPathMustExist = 0x800, FosFileMustExist = 0x1000;
        const uint SigdnFileSysPath = 0x80058000;
        const int ErrorCancelled = unchecked((int)0x800704C7);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct FilterSpec
        {
            [MarshalAs(UnmanagedType.LPWStr)] public string name;
            [MarshalAs(UnmanagedType.LPWStr)] public string spec;
        }

        [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
        class FileOpenDialogCom { }

        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItem
        {
            void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem ppsi);
            void GetDisplayName(uint sigdnName, out IntPtr ppszName);
            void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
            void Compare(IShellItem psi, uint hint, out int piOrder);
        }

        // IFileOpenDialog with every method of IModalWindow and IFileDialog repeated in vtable order, as COM interop requires.
        [ComImport, Guid("d57c7288-d4ad-4768-be02-9d969532d960"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IFileOpenDialog
        {
            [PreserveSig] int Show(IntPtr parent);
            void SetFileTypes(uint cFileTypes, [In, MarshalAs(UnmanagedType.LPArray)] FilterSpec[] rgFilterSpec);
            void SetFileTypeIndex(uint iFileType);
            void GetFileTypeIndex(out uint piFileType);
            void Advise(IntPtr pfde, out uint pdwCookie);
            void Unadvise(uint dwCookie);
            void SetOptions(uint fos);
            void GetOptions(out uint pfos);
            void SetDefaultFolder(IShellItem psi);
            void SetFolder(IShellItem psi);
            void GetFolder(out IShellItem ppsi);
            void GetCurrentSelection(out IShellItem ppsi);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetFileName(out IntPtr pszName);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
            void GetResult(out IShellItem ppsi);
            void AddPlace(IShellItem psi, int fdap);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
            void Close(int hr);
            void SetClientGuid(ref Guid guid);
            void ClearClientData();
            void SetFilter(IntPtr pFilter);
            void GetResults(out IntPtr ppenum);
            void GetSelectedItems(out IntPtr ppsai);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        static extern void SHCreateItemFromParsingName([MarshalAs(UnmanagedType.LPWStr)] string pszPath, IntPtr pbc, ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

        [DllImport("user32.dll")]
        static extern IntPtr GetActiveWindow();

        static string Modern(string title, string startFolder, bool pickFolders, string filterName, string pattern)
        {
            var dialog = (IFileOpenDialog)new FileOpenDialogCom();
            try
            {
                dialog.GetOptions(out uint options);
                options |= FosForceFileSystem | FosPathMustExist | FosNoChangeDir;
                options |= pickFolders ? FosPickFolders : FosFileMustExist;
                dialog.SetOptions(options);
                dialog.SetTitle(title);
                if (!pickFolders && !string.IsNullOrEmpty(pattern))
                    dialog.SetFileTypes(2, new[]
                    {
                        new FilterSpec { name = $"{filterName} ({pattern})", spec = pattern },
                        new FilterSpec { name = "All files (*.*)", spec = "*.*" },
                    });

                string folder = ExistingFolder(startFolder);
                if (folder != null)
                {
                    var iid = typeof(IShellItem).GUID;
                    try
                    {
                        SHCreateItemFromParsingName(folder, IntPtr.Zero, ref iid, out IShellItem item);
                        dialog.SetFolder(item);
                    }
                    catch (Exception) { } // starts in the last folder instead
                }

                int result = dialog.Show(GetActiveWindow());
                if (result == ErrorCancelled) return null;
                if (result != 0) Marshal.ThrowExceptionForHR(result);

                dialog.GetResult(out IShellItem chosen);
                chosen.GetDisplayName(SigdnFileSysPath, out IntPtr name);
                try { return Marshal.PtrToStringUni(name); }
                finally { Marshal.FreeCoTaskMem(name); }
            }
            finally
            {
                Marshal.ReleaseComObject(dialog);
            }
        }

        // ---------------------------------------------------------------- Windows: the classic dialogs (fallback)

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct OpenFileName
        {
            public int structSize;
            public IntPtr owner;
            public IntPtr instance;
            public string filter;
            public string customFilter;
            public int maxCustomFilter;
            public int filterIndex;
            public IntPtr file;
            public int maxFile;
            public IntPtr fileTitle;
            public int maxFileTitle;
            public string initialDir;
            public string title;
            public int flags;
            public short fileOffset;
            public short fileExtension;
            public string defExt;
            public IntPtr custData;
            public IntPtr hook;
            public string templateName;
            public IntPtr reserved;
            public int reserved2;
            public int flagsEx;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct BrowseInfo
        {
            public IntPtr owner;
            public IntPtr root;
            public IntPtr displayName;
            public string title;
            public uint flags;
            public IntPtr callback;
            public IntPtr param;
            public int image;
        }

        [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetOpenFileNameW")]
        static extern bool GetOpenFileName(ref OpenFileName ofn);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHBrowseForFolderW")]
        static extern IntPtr SHBrowseForFolder(ref BrowseInfo info);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHGetPathFromIDListW")]
        static extern bool SHGetPathFromIDList(IntPtr idList, IntPtr path);

        static string ClassicFile(string title, string startFolder, string filterName, string pattern)
        {
            const int max = 4096;
            IntPtr buffer = Marshal.AllocHGlobal(max * 2);
            try
            {
                Marshal.WriteInt16(buffer, 0);
                string filter = (string.IsNullOrEmpty(pattern) ? "" : $"{filterName} ({pattern})\0{pattern}\0") + "All files (*.*)\0*.*\0\0";
                var ofn = new OpenFileName
                {
                    structSize = Marshal.SizeOf(typeof(OpenFileName)),
                    owner = GetActiveWindow(),
                    filter = filter,
                    filterIndex = 1,
                    file = buffer,
                    maxFile = max,
                    initialDir = ExistingFolder(startFolder),
                    title = title,
                    flags = 0x00080000 | 0x00001000 | 0x00000800 | 0x00000008, // EXPLORER | FILEMUSTEXIST | PATHMUSTEXIST | NOCHANGEDIR
                };
                return GetOpenFileName(ref ofn) ? Marshal.PtrToStringUni(buffer) : null;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        static string ClassicFolder(string title)
        {
            IntPtr path = Marshal.AllocHGlobal(260 * 2 * 2);
            try
            {
                var info = new BrowseInfo
                {
                    owner = GetActiveWindow(),
                    title = title,
                    flags = 0x0001 | 0x0040 | 0x0010, // RETURNONLYFSDIRS | NEWDIALOGSTYLE | EDITBOX
                };
                IntPtr list = SHBrowseForFolder(ref info);
                if (list == IntPtr.Zero) return null;
                try { return SHGetPathFromIDList(list, path) ? Marshal.PtrToStringUni(path) : null; }
                finally { Marshal.FreeCoTaskMem(list); }
            }
            finally
            {
                Marshal.FreeHGlobal(path);
            }
        }
    }
}
