using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace GeekCleaner
{
    enum LeftKind { RegKey, RegValue, File, Dir, Service, Task }

    class LeftoverItem
    {
        public LeftKind Kind;
        public string Hive, KeyPath, ValueName, FsPath, Detail = "";
        public bool Checked = true;
        public string Owner; // 批量卸载时，标明属于哪个程序

        public string HiveFull { get { return Hive == "HKLM" ? "HKEY_LOCAL_MACHINE" : "HKEY_CURRENT_USER"; } }

        public string Display
        {
            get
            {
                switch (Kind)
                {
                    case LeftKind.RegKey: return HiveFull + "\\" + KeyPath;
                    case LeftKind.RegValue: return HiveFull + "\\" + KeyPath + "  →  [" + (ValueName == "" ? "(默认)" : ValueName) + "]";
                    case LeftKind.Service: return "服务: " + FsPath;
                    case LeftKind.Task: return "计划任务: " + FsPath;
                    default: return FsPath;
                }
            }
        }

        public string TypeText
        {
            get
            {
                switch (Kind)
                {
                    case LeftKind.RegKey: return "注册表项";
                    case LeftKind.RegValue: return "注册表值";
                    case LeftKind.Service: return "系统服务";
                    case LeftKind.Task: return "计划任务";
                    case LeftKind.Dir: return "文件夹";
                    default: return "文件";
                }
            }
        }
    }

    class DeleteResult
    {
        public List<string> Failed = new List<string>();
        public long Freed;
        public int Done;
        public string BackupFile;
    }

    /// <summary>扫描一个程序在注册表、磁盘、服务、计划任务中的全部关联项。</summary>
    class LeftoverScanner
    {
        static readonly HashSet<string> Blacklist = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "microsoft", "microsoft corporation", "windows", "system", "system32", "common", "program", "programs",
            "software", "update", "updater", "setup", "install", "installer", "uninstall", "driver", "drivers",
            "classes", "policies", "wow6432node", "clients", "user", "users", "data", "temp", "cache", "default",
            "app", "apps", "application", "applications", "tools", "tool", "game", "games", "player", "media",
            "office", "server", "client", "service", "services", "runtime", "desktop", "launcher", "python", "java",
            "bin", "x64", "x86", "current", "release", "main", "core", "help", "config", "settings", "local",
            "roaming", "shell", "explorer", "internet", "network", "security", "google", "intel", "nvidia", "amd",
            "adobe", "apple", "mozilla", "registeredapplications", "odbc", "khronos", "node", "net", ".net", "sdk",
            "web", "browser", "video", "audio", "music", "driver", "fonts", "packages", "plugins", "resources"
        };

        static readonly HashSet<string> SkipKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData",
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing",
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\SideBySide",
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Appx",
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\AppModel",
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Perflib",
            @"SOFTWARE\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel",
            @"SOFTWARE\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppContainer"
        };

        const int MaxItems = 5000;

        readonly AppEntry app;
        readonly string inst;           // 可用于路径匹配的安装目录（已校验安全），否则为 null
        readonly string publisher;
        readonly HashSet<string> terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        List<LeftoverItem> items;
        HashSet<string> addedKeys;
        List<string> addedDirs;
        Action<string> progress;
        CancellationToken ct;
        int counter;

        public LeftoverScanner(AppEntry a)
        {
            app = a;
            if (a.InstallLocation != null && a.InstallLocation.Length >= 8 && Util.IsSafePath(a.InstallLocation))
                inst = a.InstallLocation;

            string clean = CleanName(a.Name);
            AddTerm(clean);
            AddTerm(clean.Replace(" ", ""));
            if (inst != null && !a.LocationGuessed) AddTerm(Path.GetFileName(inst));
            if (a.KeyName != null && a.Kind != AppKind.Store)
            {
                AddTerm(a.KeyName);
                if (a.KeyName.EndsWith("_is1", StringComparison.OrdinalIgnoreCase))
                    AddTerm(a.KeyName.Substring(0, a.KeyName.Length - 4));
            }
            string pub = (a.Publisher ?? "").Trim();
            if (pub.Length >= 3 && !Blacklist.Contains(pub) && !terms.Contains(pub)) publisher = pub;
        }

        public IEnumerable<string> Terms { get { return terms; } }

        public static string CleanName(string name)
        {
            string s = Regex.Replace(name ?? "", @"\s*[\(\[（][^\)\]）]*[\)\]）]", "");
            for (int i = 0; i < 3; i++)
            {
                s = Regex.Replace(s, @"\s+(x64|x86|64-bit|32-bit|64位|32位)\s*$", "", RegexOptions.IgnoreCase);
                s = Regex.Replace(s, @"[\s\-_]*\b(version\s*|ver\.?\s*|v)?\d+([\.\-_]\d+)*\s*$", "", RegexOptions.IgnoreCase);
            }
            return s.Trim();
        }

        void AddTerm(string s)
        {
            if (s == null) return;
            s = s.Trim();
            bool cjk = s.Any(c => c >= 0x2E80);
            if (s.Length < (cjk ? 2 : 3)) return;
            if (Blacklist.Contains(s)) return;
            if (s.All(c => char.IsDigit(c) || c == '.' || c == ' ')) return;
            terms.Add(s);
        }

        bool NameMatches(string keyName)
        {
            if (terms.Contains(keyName)) return true;
            if (keyName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && keyName.Length > 4)
                return terms.Contains(keyName.Substring(0, keyName.Length - 4));
            return false;
        }

        void Add(LeftoverItem it)
        {
            lock (items) if (items.Count < MaxItems) items.Add(it);
        }

        void Tick(string what)
        {
            ct.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref counter) % 400 == 0 && progress != null) progress("正在扫描残留: " + what);
        }

        public List<LeftoverItem> Scan(Action<string> progress, CancellationToken ct)
        {
            this.progress = progress;
            this.ct = ct;
            items = new List<LeftoverItem>();
            addedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            addedDirs = new List<string>();

            // 1. 程序自身的卸载注册表项
            if (app.RegHive != null && app.RegPath != null)
            {
                using (var bk = OpenHive(app.RegHive))
                using (var k = bk.OpenSubKey(app.RegPath))
                    if (k != null) AddKey(app.RegHive, app.RegPath, "卸载信息");
            }

            // 2. 全面遍历注册表 HKCU\Software 与 HKLM\SOFTWARE（含 WOW6432Node、Classes）
            try
            {
                Parallel.ForEach(new[] { "HKCU", "HKLM" }, delegate(string hive)
                {
                    using (var bk = OpenHive(hive))
                    using (var k = bk.OpenSubKey("SOFTWARE"))
                        if (k != null) Walk(k, hive, "SOFTWARE", 0);
                });
            }
            catch (AggregateException ae)
            {
                if (ae.Flatten().InnerExceptions.Any(x => x is OperationCanceledException)) throw new OperationCanceledException();
                throw;
            }

            ScanServices();
            ScanFirewall();
            ScanTasks();
            ScanFiles();
            return items;
        }

        public static RegistryKey OpenHive(string hive)
        {
            return RegistryKey.OpenBaseKey(hive == "HKLM" ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, RegistryView.Registry64);
        }

        void AddKey(string hive, string path, string detail)
        {
            lock (addedKeys) if (!addedKeys.Add(hive + "\\" + path)) return;
            Add(new LeftoverItem { Kind = LeftKind.RegKey, Hive = hive, KeyPath = path, Detail = detail });
        }

        void Walk(RegistryKey key, string hive, string path, int depth)
        {
            Tick(hive + "\\" + path);
            string[] subs;
            try { subs = key.GetSubKeyNames(); } catch { return; }

            // 值：数据或名称中引用了安装目录
            if (inst != null)
            {
                string[] names = null;
                try { names = key.GetValueNames(); } catch { }
                if (names != null)
                {
                    foreach (string n in names)
                    {
                        try
                        {
                            string shown = null;
                            if (Util.ContainsPath(n, inst)) shown = n;
                            else
                            {
                                object v = key.GetValue(n, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                                string s = v as string;
                                if (s != null) { if (Util.ContainsPath(s, inst)) shown = s; }
                                else
                                {
                                    var arr = v as string[];
                                    if (arr != null)
                                        foreach (string x in arr)
                                            if (Util.ContainsPath(x, inst)) { shown = x; break; }
                                }
                            }
                            if (shown != null && n == "" && path.EndsWith("\\command", StringComparison.OrdinalIgnoreCase))
                            {
                                // ...\shell\<动作>\command 指向本程序：整个右键菜单/打开方式动作项一并删除
                                string verb = path.Substring(0, path.Length - 8);
                                int cut = verb.LastIndexOf('\\');
                                if (cut > 0 && verb.Substring(0, cut).EndsWith("\\shell", StringComparison.OrdinalIgnoreCase))
                                {
                                    AddKey(hive, verb, "右键菜单/打开方式: " + (shown.Length > 120 ? shown.Substring(0, 120) + "…" : shown));
                                    shown = null;
                                }
                            }
                            if (shown != null)
                            {
                                if (shown.Length > 160) shown = shown.Substring(0, 160) + "…";
                                Add(new LeftoverItem { Kind = LeftKind.RegValue, Hive = hive, KeyPath = path, ValueName = n, Detail = "引用安装目录: " + shown });
                            }
                        }
                        catch { }
                    }
                }
            }

            // 顶层两级并行遍历（Classes、Microsoft、WOW6432Node 等大分支同时扫描）
            if (depth <= 1) Parallel.ForEach(subs, delegate(string sub) { Visit(key, hive, path, depth, sub); });
            else foreach (string sub in subs) Visit(key, hive, path, depth, sub);
        }

        void Visit(RegistryKey key, string hive, string path, int depth, string sub)
        {
            string subPath = path + "\\" + sub;
            if (SkipKeys.Contains(subPath)) return;
            lock (addedKeys) if (addedKeys.Contains(hive + "\\" + subPath)) return;
            int d = depth + 1;

            if (d <= 4 && NameMatches(sub))
            {
                AddKey(hive, subPath, "名称与程序匹配");
                return;
            }
            if (d <= 2 && publisher != null && string.Equals(sub, publisher, StringComparison.OrdinalIgnoreCase))
            {
                // 发布者项：只有当其下全部子项都属于本程序时才整体删除
                try
                {
                    using (var pk = key.OpenSubKey(sub))
                    {
                        if (pk != null)
                        {
                            string[] ps = pk.GetSubKeyNames();
                            if (ps.Length > 0 && ps.All(NameMatches))
                            {
                                AddKey(hive, subPath, "发布者项（仅包含本程序）");
                                return;
                            }
                        }
                    }
                }
                catch { }
            }
            if (inst == null && d >= 4) return; // 没有安装目录可匹配时无需深入

            RegistryKey sk = null;
            try { sk = key.OpenSubKey(sub); } catch { }
            if (sk == null) return;
            try { Walk(sk, hive, subPath, d); }
            finally { sk.Dispose(); }
        }

        void ScanServices()
        {
            const string root = @"SYSTEM\CurrentControlSet\Services";
            try
            {
                using (var bk = OpenHive("HKLM"))
                using (var k = bk.OpenSubKey(root))
                {
                    if (k == null) return;
                    foreach (string sub in k.GetSubKeyNames())
                    {
                        Tick("服务 " + sub);
                        try
                        {
                            using (var s = k.OpenSubKey(sub))
                            {
                                if (s == null) continue;
                                string img = s.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                                if (img == null) continue;
                                if (inst != null && Util.ContainsPath(img, inst))
                                    Add(new LeftoverItem { Kind = LeftKind.Service, FsPath = sub, Detail = img });
                            }
                        }
                        catch (OperationCanceledException) { throw; }
                        catch { }
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { }
        }

        void ScanFirewall()
        {
            if (inst == null) return;
            const string path = @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules";
            try
            {
                using (var bk = OpenHive("HKLM"))
                using (var k = bk.OpenSubKey(path))
                {
                    if (k == null) return;
                    foreach (string n in k.GetValueNames())
                    {
                        string s = k.GetValue(n) as string;
                        if (s != null && Util.ContainsPath(s, inst))
                            Add(new LeftoverItem { Kind = LeftKind.RegValue, Hive = "HKLM", KeyPath = path, ValueName = n, Detail = "防火墙规则" });
                    }
                }
            }
            catch { }
        }

        void ScanTasks()
        {
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "Tasks");
            if (!Directory.Exists(root)) return;
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                string d = stack.Pop();
                try
                {
                    foreach (string sub in Directory.GetDirectories(d))
                    {
                        if (d == root && string.Equals(Path.GetFileName(sub), "Microsoft", StringComparison.OrdinalIgnoreCase)) continue;
                        stack.Push(sub);
                    }
                    foreach (string f in Directory.GetFiles(d))
                    {
                        Tick("计划任务");
                        try
                        {
                            bool hit = false;
                            if (inst != null && new FileInfo(f).Length < 1024 * 1024)
                                hit = Util.ContainsPath(File.ReadAllText(f), inst);
                            if (hit)
                                Add(new LeftoverItem { Kind = LeftKind.Task, FsPath = f.Substring(root.Length), Detail = "任务指向安装目录" });
                        }
                        catch (OperationCanceledException) { throw; }
                        catch { }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch { }
            }
        }

        void AddPath(string p, string detail)
        {
            p = Util.Norm(p);
            if (p == null || !Util.IsSafePath(p)) return;
            foreach (string d in addedDirs)
                if (Util.IsUnder(p, d)) return;
            bool isDir = Directory.Exists(p);
            if (!isDir && !File.Exists(p)) return;
            if (isDir) addedDirs.Add(p);
            Add(new LeftoverItem { Kind = isDir ? LeftKind.Dir : LeftKind.File, FsPath = p, Detail = detail });
        }

        void ScanDirRoot(string root, string label)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return;
            string[] dirs;
            try { dirs = Directory.GetDirectories(root); } catch { return; }
            foreach (string dir in dirs)
            {
                Tick(dir);
                string name = Path.GetFileName(dir);
                if (terms.Contains(name)) { AddPath(dir, label); continue; }
                if (publisher != null && string.Equals(name, publisher, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        string[] subs = Directory.GetDirectories(dir);
                        string[] files = Directory.GetFiles(dir);
                        var matched = subs.Where(s => terms.Contains(Path.GetFileName(s))).ToList();
                        if (files.Length == 0 && subs.Length > 0 && matched.Count == subs.Length) AddPath(dir, label + "（发布者文件夹，仅含本程序）");
                        else foreach (string m in matched) AddPath(m, label);
                    }
                    catch { }
                }
            }
        }

        void ScanFiles()
        {
            if (inst != null && Directory.Exists(inst))
                AddPath(inst, app.LocationGuessed ? "安装目录（推断得出，请确认）" : "安装目录");

            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string prof = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

            ScanDirRoot(roaming, "用户数据 (AppData\\Roaming)");
            ScanDirRoot(local, "用户数据 (AppData\\Local)");
            ScanDirRoot(Path.Combine(prof, "AppData", "LocalLow"), "用户数据 (AppData\\LocalLow)");
            ScanDirRoot(Path.Combine(local, "Programs"), "用户程序目录");
            ScanDirRoot(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "公共数据 (ProgramData)");
            ScanDirRoot(pf, "Program Files");
            ScanDirRoot(pf86, "Program Files (x86)");
            ScanDirRoot(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles), "Common Files");
            ScanDirRoot(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86), "Common Files (x86)");
            ScanDirRoot(Path.Combine(local, "VirtualStore", "Program Files"), "VirtualStore");
            ScanDirRoot(Path.Combine(local, "VirtualStore", "Program Files (x86)"), "VirtualStore");

            // 开始菜单文件夹 + 指向安装目录的快捷方式
            string[] lnkRoots = {
                Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
                Path.Combine(roaming, @"Microsoft\Internet Explorer\Quick Launch")
            };
            ScanDirRoot(lnkRoots[0], "开始菜单文件夹");
            ScanDirRoot(lnkRoots[1], "开始菜单文件夹");
            if (inst == null) return;
            Encoding latin = Encoding.GetEncoding(28591);
            foreach (string r in lnkRoots)
            {
                if (string.IsNullOrEmpty(r) || !Directory.Exists(r)) continue;
                var stack = new Stack<string>();
                stack.Push(r);
                while (stack.Count > 0)
                {
                    string d = stack.Pop();
                    try
                    {
                        foreach (string sub in Directory.GetDirectories(d)) stack.Push(sub);
                        foreach (string f in Directory.GetFiles(d, "*.lnk"))
                        {
                            Tick(f);
                            try
                            {
                                byte[] b = File.ReadAllBytes(f);
                                if (b.Length > 1024 * 1024) continue;
                                if (Util.ContainsPath(latin.GetString(b), inst) || Util.ContainsPath(Encoding.Unicode.GetString(b), inst) ||
                                    (b.Length > 1 && Util.ContainsPath(Encoding.Unicode.GetString(b, 1, b.Length - 1), inst)))
                                    AddPath(f, "快捷方式");
                            }
                            catch (OperationCanceledException) { throw; }
                            catch { }
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { }
                }
            }
        }

        // ================= 删除 =================

        static string Esc(string s) { return s.Replace("\\", "\\\\").Replace("\"", "\\\""); }

        static string Hex(byte[] b)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < b.Length; i++) { if (i > 0) sb.Append(','); sb.Append(b[i].ToString("x2")); }
            return sb.ToString();
        }

        static string RegLine(string name, object v, RegistryValueKind kind)
        {
            string n = name == "" ? "@" : "\"" + Esc(name) + "\"";
            switch (kind)
            {
                case RegistryValueKind.String: return n + "=\"" + Esc((string)v) + "\"";
                case RegistryValueKind.DWord: return n + "=dword:" + ((int)v).ToString("x8");
                case RegistryValueKind.QWord: return n + "=hex(b):" + Hex(BitConverter.GetBytes((long)v));
                case RegistryValueKind.ExpandString: return n + "=hex(2):" + Hex(Encoding.Unicode.GetBytes((string)v + "\0"));
                case RegistryValueKind.MultiString: return n + "=hex(7):" + Hex(Encoding.Unicode.GetBytes(string.Join("\0", (string[])v) + "\0\0"));
                case RegistryValueKind.Binary: return n + "=hex:" + Hex((byte[])v);
                default: return ";" + n + " (未备份的类型 " + kind + ")";
            }
        }

        static string Backup(List<LeftoverItem> sel, string appName)
        {
            var reg = sel.Where(i => i.Kind == LeftKind.RegKey || i.Kind == LeftKind.RegValue).ToList();
            if (reg.Count == 0) return null;
            Directory.CreateDirectory(Util.BackupRoot);
            string safe = Regex.Replace(appName, @"[^\w\-\. ]", "_");
            if (safe.Length > 40) safe = safe.Substring(0, 40);
            string file = Path.Combine(Util.BackupRoot, DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + safe + ".reg");
            var sb = new StringBuilder("Windows Registry Editor Version 5.00\r\n\r\n");
            foreach (var it in reg)
            {
                try
                {
                    if (it.Kind == LeftKind.RegKey)
                    {
                        string tmp = Path.GetTempFileName();
                        Util.RunWait("reg.exe", "export \"" + it.HiveFull + "\\" + it.KeyPath + "\" \"" + tmp + "\" /y", true);
                        string text = File.ReadAllText(tmp, Encoding.Unicode);
                        File.Delete(tmp);
                        int nl = text.IndexOf('\n');
                        if (nl >= 0) sb.Append(text.Substring(nl + 1).TrimStart('\r', '\n')).Append("\r\n");
                    }
                    else
                    {
                        using (var bk = OpenHive(it.Hive))
                        using (var k = bk.OpenSubKey(it.KeyPath))
                        {
                            if (k == null) continue;
                            object v = k.GetValue(it.ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                            if (v == null) continue;
                            sb.Append("[" + it.HiveFull + "\\" + it.KeyPath + "]\r\n");
                            sb.Append(RegLine(it.ValueName, v, k.GetValueKind(it.ValueName))).Append("\r\n\r\n");
                        }
                    }
                }
                catch { }
            }
            File.WriteAllText(file, sb.ToString(), Encoding.Unicode);
            return file;
        }

        /// <summary>值被删除后，如果所在的项已经完全为空，则一并向上清理空项。</summary>
        static void CleanupEmpty(RegistryKey bk, string path)
        {
            for (int i = 0; i < 5; i++)
            {
                if (path.Split('\\').Length < 3) return;
                using (var k = bk.OpenSubKey(path))
                {
                    if (k == null) return;
                    if (k.ValueCount != 0 || k.SubKeyCount != 0) return;
                }
                bk.DeleteSubKey(path, false);
                path = path.Substring(0, path.LastIndexOf('\\'));
            }
        }

        public static DeleteResult Delete(List<LeftoverItem> sel, string appName, Action<string> progress)
        {
            var r = new DeleteResult();
            try { r.BackupFile = Backup(sel, appName); }
            catch (Exception ex) { r.Failed.Add("注册表备份失败: " + ex.Message); }

            // 先结束仍在运行的进程
            foreach (var it in sel.Where(i => i.Kind == LeftKind.Dir))
                Util.KillProcessesUnder(it.FsPath);

            var order = new[] { LeftKind.Service, LeftKind.Task, LeftKind.RegValue, LeftKind.RegKey, LeftKind.File, LeftKind.Dir };
            foreach (LeftKind kind in order)
            {
                foreach (var it in sel.Where(i => i.Kind == kind).OrderByDescending(i => (i.KeyPath ?? i.FsPath ?? "").Length))
                {
                    if (progress != null) progress("正在删除: " + it.Display);
                    try
                    {
                        switch (kind)
                        {
                            case LeftKind.Service:
                                Util.RunWait("sc.exe", "stop \"" + it.FsPath + "\"", true);
                                if (Util.RunWait("sc.exe", "delete \"" + it.FsPath + "\"", true) != 0) r.Failed.Add(it.Display);
                                else r.Done++;
                                break;
                            case LeftKind.Task:
                                if (Util.RunWait("schtasks.exe", "/delete /tn \"" + it.FsPath + "\" /f", true) != 0) r.Failed.Add(it.Display);
                                else r.Done++;
                                break;
                            case LeftKind.RegValue:
                                using (var bk = OpenHive(it.Hive))
                                {
                                    using (var k = bk.OpenSubKey(it.KeyPath, true))
                                        if (k != null) k.DeleteValue(it.ValueName, false);
                                    CleanupEmpty(bk, it.KeyPath);
                                }
                                r.Done++;
                                break;
                            case LeftKind.RegKey:
                                using (var bk = OpenHive(it.Hive))
                                    bk.DeleteSubKeyTree(it.KeyPath, false);
                                r.Done++;
                                break;
                            default:
                                if (!Util.IsSafePath(it.FsPath)) { r.Failed.Add(it.FsPath + "  （受保护的路径，已跳过）"); break; }
                                int before = r.Failed.Count;
                                r.Freed += Util.DeleteTree(it.FsPath, r.Failed, true);
                                if (r.Failed.Count == before) r.Done++;
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        r.Failed.Add(it.Display + "  （" + ex.Message.Trim() + "）");
                    }
                }
            }
            return r;
        }
    }
}
