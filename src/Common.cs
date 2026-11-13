using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace GeekCleaner
{
    static class Native
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool MoveFileEx(string existing, string newName, int flags);

        // 64 位进程下的布局（本程序以 x64 编译）
        [StructLayout(LayoutKind.Sequential)]
        public struct SHQUERYRBINFO { public int cbSize; public long i64Size; public long i64NumItems; }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern int SHQueryRecycleBin(string root, ref SHQUERYRBINFO info);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern int SHEmptyRecycleBin(IntPtr hwnd, string root, int flags);
    }

    static class Util
    {
        static List<string> prot;
        static string winDir;

        public static string BackupRoot
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\'), "Backups"); }
        }

        public static string FormatSize(long b)
        {
            if (b < 0) return "";
            if (b < 1024) return b + " B";
            double v = b / 1024.0;
            if (v < 1024) return v.ToString("0.0") + " KB";
            v /= 1024;
            if (v < 1024) return v.ToString("0.0") + " MB";
            v /= 1024;
            return v.ToString("0.00") + " GB";
        }

        /// <summary>规范化路径：去引号、展开环境变量、去掉结尾反斜杠。无效返回 null。</summary>
        public static string Norm(string p)
        {
            if (string.IsNullOrWhiteSpace(p)) return null;
            try
            {
                p = Environment.ExpandEnvironmentVariables(p.Trim().Trim('"').Trim());
                if (p.Length < 3 || p[1] != ':') return null;
                p = Path.GetFullPath(p);
                if (p.Length > 3) p = p.TrimEnd('\\');
                return p;
            }
            catch { return null; }
        }

        static void InitProt()
        {
            var l = new List<string>();
            Environment.SpecialFolder[] sf = {
                Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles,
                Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.CommonProgramFiles,
                Environment.SpecialFolder.CommonProgramFilesX86, Environment.SpecialFolder.CommonApplicationData,
                Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.ApplicationData,
                Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.DesktopDirectory,
                Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.CommonDesktopDirectory,
                Environment.SpecialFolder.StartMenu, Environment.SpecialFolder.CommonStartMenu,
                Environment.SpecialFolder.Programs, Environment.SpecialFolder.CommonPrograms,
                Environment.SpecialFolder.Startup, Environment.SpecialFolder.CommonStartup,
                Environment.SpecialFolder.System, Environment.SpecialFolder.MyPictures,
                Environment.SpecialFolder.MyMusic, Environment.SpecialFolder.MyVideos,
                Environment.SpecialFolder.CommonDocuments, Environment.SpecialFolder.Fonts
            };
            foreach (var f in sf)
            {
                string p = Norm(Environment.GetFolderPath(f));
                if (p != null) l.Add(p);
            }
            string prof = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            l.Add(Path.Combine(prof, "Downloads"));
            l.Add(Path.Combine(prof, "AppData"));
            l.Add(Path.Combine(prof, "AppData", "LocalLow"));
            l.Add(Path.Combine(local, "Programs"));
            l.Add(Path.Combine(local, "Temp"));
            l.Add(Path.Combine(local, "Packages"));
            l.Add(Path.Combine(local, "Microsoft"));
            string users = Path.GetDirectoryName(prof);
            if (users != null) l.Add(users);
            foreach (var d in FixedDrives())
            {
                l.Add(d + "Program Files");
                l.Add(d + "Program Files (x86)");
                l.Add(d + "Users");
                l.Add(d + "Windows");
                l.Add(d + "ProgramData");
            }
            winDir = Norm(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            prot = l;
        }

        public static List<string> FixedDrives()
        {
            var r = new List<string>();
            foreach (var d in DriveInfo.GetDrives())
            {
                try { if (d.DriveType == DriveType.Fixed && d.IsReady) r.Add(d.Name); } catch { }
            }
            return r;
        }

        /// <summary>该路径是否可以作为“整个删除”的目标（排除盘符根目录、系统目录及其上级）。</summary>
        public static bool IsSafePath(string p)
        {
            if (prot == null) InitProt();
            p = Norm(p);
            if (p == null || p.Length <= 3) return false;
            string pp = p + "\\";
            foreach (string s in prot)
            {
                if (string.Equals(s, p, StringComparison.OrdinalIgnoreCase)) return false;
                if (s.StartsWith(pp, StringComparison.OrdinalIgnoreCase)) return false;
            }
            if (winDir != null && pp.StartsWith(winDir + "\\", StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        /// <summary>text 中是否包含 path（要求 path 之后是路径边界，避免 "App" 匹配到 "App2"）。</summary>
        public static bool ContainsPath(string text, string path)
        {
            if (string.IsNullOrEmpty(text) || text.Length < path.Length) return false;
            int i = 0;
            while ((i = text.IndexOf(path, i, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                int e = i + path.Length;
                if (e >= text.Length) return true;
                char c = text[e];
                if (c == '\\' || c == '"' || c == '\'' || c == ',' || c == ';' || c == '|' || c == '/' ||
                    c == '<' || c == '\0' || c == '\r' || c == '\n') return true;
                i = e;
            }
            return false;
        }

        public static bool IsUnder(string child, string parent)
        {
            if (child == null || parent == null) return false;
            if (string.Equals(child, parent, StringComparison.OrdinalIgnoreCase)) return true;
            return child.StartsWith(parent.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
        }

        public static long DirSize(string path, CancellationToken ct)
        {
            long total = 0;
            var stack = new Stack<string>();
            stack.Push(path);
            while (stack.Count > 0)
            {
                if (ct.IsCancellationRequested) break;
                string d = stack.Pop();
                try
                {
                    foreach (var fsi in new DirectoryInfo(d).EnumerateFileSystemInfos())
                    {
                        if ((fsi.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                        var fi = fsi as FileInfo;
                        if (fi != null) total += fi.Length; else stack.Push(fsi.FullName);
                    }
                }
                catch { }
            }
            return total;
        }

        /// <summary>彻底删除文件或文件夹，返回释放的字节数。失败项写入 failed。</summary>
        public static long DeleteTree(string path, List<string> failed, bool rebootFallback)
        {
            long freed = 0;
            try
            {
                FileAttributes a = File.GetAttributes(path);
                if ((a & FileAttributes.Directory) == 0)
                {
                    long len = new FileInfo(path).Length;
                    if ((a & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, FileAttributes.Normal);
                    File.Delete(path);
                    return len;
                }
                if ((a & FileAttributes.ReparsePoint) != 0)
                {
                    Directory.Delete(path); // 只删除链接本身，不跟随
                    return 0;
                }
                foreach (string e in Directory.GetFileSystemEntries(path))
                    freed += DeleteTree(e, failed, rebootFallback);
                if ((a & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, FileAttributes.Directory);
                Directory.Delete(path);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            catch (Exception ex)
            {
                if (rebootFallback && Native.MoveFileEx(path, null, 4))
                    failed.Add(path + "  （已安排在重启后删除）");
                else
                    failed.Add(path + "  （" + ex.Message.Trim() + "）");
            }
            return freed;
        }

        public static int RunWait(string exe, string args, bool hidden)
        {
            try
            {
                var psi = new ProcessStartInfo(exe, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = hidden;
                using (var p = Process.Start(psi))
                {
                    p.WaitForExit();
                    return p.ExitCode;
                }
            }
            catch { return -1; }
        }

        public static string RunCapture(string exe, string args, Encoding enc)
        {
            try
            {
                var psi = new ProcessStartInfo(exe, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.StandardOutputEncoding = enc;
                using (var p = Process.Start(psi))
                {
                    string s = p.StandardOutput.ReadToEnd();
                    p.WaitForExit();
                    return s;
                }
            }
            catch { return ""; }
        }

        /// <summary>从命令行字符串里取出 exe 路径和参数。</summary>
        public static bool SplitCommand(string cmd, out string exe, out string args)
        {
            exe = null; args = "";
            if (string.IsNullOrWhiteSpace(cmd)) return false;
            cmd = cmd.Trim();
            if (cmd[0] == '"')
            {
                int q = cmd.IndexOf('"', 1);
                if (q < 0) return false;
                exe = cmd.Substring(1, q - 1);
                args = cmd.Substring(q + 1).Trim();
                return true;
            }
            int i = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return false;
            exe = cmd.Substring(0, i + 4);
            args = cmd.Substring(i + 4).Trim();
            return true;
        }

        public static void KillProcessesUnder(string dir)
        {
            int me = Process.GetCurrentProcess().Id;
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.Id == me || p.Id <= 4) continue;
                    string f = p.MainModule.FileName;
                    if (IsUnder(f, dir))
                    {
                        p.Kill();
                        p.WaitForExit(3000);
                    }
                }
                catch { }
                finally { p.Dispose(); }
            }
        }
    }
}
