using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace GeekCleaner
{
    enum AppKind { Machine64, Machine32, User, Portable, Store }

    class AppEntry
    {
        public string Name = "", Version = "", Publisher = "", InstallDate = "";
        public string InstallLocation, UninstallString, ModifyPath, DisplayIcon;
        public string KeyName, RegHive, RegPath, PackageFullName, IconKey;
        public bool LocationGuessed, IsMsi;
        public long Size = -1;
        public AppKind Kind;
        public object IconImg; // 界面层缓存的图标 (System.Drawing.Image)
        public DateTime LastUsed = DateTime.MinValue; // 来自系统的启动记录；MinValue 表示没有记录
        public int RunCount;
        public int Advice;            // 0 无, 1 建议卸载, 2 可考虑卸载, 3 常用, 4 驱动/运行库
        public string AdviceText = "", AdviceWhy = "";

        public int IdleDays { get { return LastUsed == DateTime.MinValue ? -1 : Math.Max(0, (int)(DateTime.Now - LastUsed).TotalDays); } }

        public string UsedText
        {
            get
            {
                int d = IdleDays;
                if (d < 0) return "无记录";
                if (d == 0) return "今天";
                if (d < 60) return d + " 天前";
                if (d < 730) return (d / 30) + " 个月前";
                return (d / 365) + " 年前";
            }
        }

        public string KindText
        {
            get
            {
                switch (Kind)
                {
                    case AppKind.Machine64: return "64 位";
                    case AppKind.Machine32: return "32 位";
                    case AppKind.User: return "当前用户";
                    case AppKind.Portable: return "便携/未注册";
                    default: return "商店应用";
                }
            }
        }
    }

    static class AppScanner
    {
        const string UN = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        const string UN32 = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

        public static List<AppEntry> ScanAll()
        {
            var list = new List<AppEntry>();
            ReadUninstall(list, RegistryHive.LocalMachine, "HKLM", UN, AppKind.Machine64);
            ReadUninstall(list, RegistryHive.LocalMachine, "HKLM", UN32, AppKind.Machine32);
            ReadUninstall(list, RegistryHive.CurrentUser, "HKCU", UN, AppKind.User);
            ReadUninstall(list, RegistryHive.CurrentUser, "HKCU", UN32, AppKind.User);
            try { ScanPortable(list); } catch { }
            try { ScanStore(list); } catch { }
            try { Usage.Fill(list); } catch { }
            foreach (var a in list) Advise(a);
            return list;
        }

        static readonly Regex KeepName = new Regex(@"redistributable|runtime|driver|驱动|\.net|webview|visual c\+\+|windows sdk|update health|chipset|firmware|运行库|运行时",
            RegexOptions.IgnoreCase);
        static readonly Regex KeepPub = new Regex(@"\bintel\b|nvidia|advanced micro|realtek|\bamd\b", RegexOptions.IgnoreCase);

        /// <summary>综合使用频率与占用大小，给出是否值得卸载的建议。</summary>
        public static void Advise(AppEntry a)
        {
            a.Advice = 0; a.AdviceText = ""; a.AdviceWhy = "";
            if (KeepName.IsMatch(a.Name) || KeepPub.IsMatch(a.Publisher))
            {
                a.Advice = 4; a.AdviceText = "驱动/运行库"; a.AdviceWhy = "属于驱动或运行库，其他程序可能依赖它，建议保留";
                return;
            }
            int d = a.IdleDays;
            if (d < 0) return;
            // 系统自带的微软商店应用（应用商店、记事本等）不给卸载建议
            bool builtIn = a.Kind == AppKind.Store && a.Publisher.IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) >= 0;
            if (builtIn && d > 30) return;
            const long MB = 1024 * 1024;
            if (d >= 180 && a.Size >= 500 * MB) { a.Advice = 1; a.AdviceText = "建议卸载"; a.AdviceWhy = "超过半年没有启动过，且占用空间较大"; }
            else if (d >= 180) { a.Advice = 2; a.AdviceText = "可考虑卸载"; a.AdviceWhy = "超过半年没有启动过"; }
            else if (d >= 90 && a.Size >= 1024 * MB) { a.Advice = 2; a.AdviceText = "可考虑卸载"; a.AdviceWhy = "三个月以上没有启动过，且占用超过 1 GB"; }
            else if (d <= 30) { a.Advice = 3; a.AdviceText = "常用"; a.AdviceWhy = "最近一个月内使用过"; }
        }

        static int Int(RegistryKey k, string name)
        {
            object v = k.GetValue(name);
            return v is int ? (int)v : 0;
        }

        static string Str(RegistryKey k, string name)
        {
            object v = k.GetValue(name);
            return v == null ? null : v.ToString().Trim();
        }

        static void ReadUninstall(List<AppEntry> list, RegistryHive hive, string hiveName, string path, AppKind kind)
        {
            try
            {
                using (var bk = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64))
                using (var k = bk.OpenSubKey(path))
                {
                    if (k == null) return;
                    foreach (string sub in k.GetSubKeyNames())
                    {
                        try
                        {
                            using (var s = k.OpenSubKey(sub))
                            {
                                if (s == null) continue;
                                string name = Str(s, "DisplayName");
                                if (string.IsNullOrWhiteSpace(name)) continue;
                                if (Int(s, "SystemComponent") == 1) continue;
                                if (s.GetValue("ParentKeyName") != null) continue;

                                var a = new AppEntry();
                                a.Name = name;
                                a.Version = Str(s, "DisplayVersion") ?? "";
                                a.Publisher = Str(s, "Publisher") ?? "";
                                a.UninstallString = Str(s, "UninstallString");
                                a.ModifyPath = Str(s, "ModifyPath");
                                a.DisplayIcon = Str(s, "DisplayIcon");
                                a.KeyName = sub;
                                a.RegHive = hiveName;
                                a.RegPath = path + "\\" + sub;
                                a.Kind = kind;
                                a.IsMsi = Int(s, "WindowsInstaller") == 1 ||
                                          (a.UninstallString != null && a.UninstallString.IndexOf("msiexec", StringComparison.OrdinalIgnoreCase) >= 0 && sub.StartsWith("{"));
                                int kb = Int(s, "EstimatedSize");
                                if (kb > 0) a.Size = (long)kb * 1024;
                                string d = Str(s, "InstallDate");
                                if (d != null && d.Length == 8 && d.All(char.IsDigit))
                                    a.InstallDate = d.Substring(0, 4) + "-" + d.Substring(4, 2) + "-" + d.Substring(6, 2);
                                GuessLocation(a, Str(s, "InstallLocation"));
                                list.Add(a);
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        static void GuessLocation(AppEntry a, string regLoc)
        {
            string loc = Util.Norm(regLoc);
            if (loc != null && Util.IsSafePath(loc) && Directory.Exists(loc))
            {
                a.InstallLocation = loc;
                return;
            }
            // 注册表没写安装位置：从图标或卸载程序所在目录推断
            var cands = new List<string>();
            if (a.DisplayIcon != null)
                cands.Add(Regex.Replace(a.DisplayIcon.Trim().Trim('"'), @",\s*-?\d+$", "").Trim('"'));
            string exe, args;
            if (Util.SplitCommand(a.UninstallString, out exe, out args)) cands.Add(exe);
            foreach (string c in cands)
            {
                string f = Util.Norm(c);
                if (f == null || !File.Exists(f)) continue;
                string dir = Path.GetDirectoryName(f);
                if (dir != null && Util.IsSafePath(dir))
                {
                    a.InstallLocation = dir;
                    a.LocationGuessed = true;
                    return;
                }
            }
        }

        // ---------- 便携 / 未注册程序 ----------

        static readonly HashSet<string> SkipNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "Windows", "Users", "Program Files", "Program Files (x86)", "ProgramData", "PerfLogs", "Recovery",
            "System Volume Information", "Documents and Settings", "Config.Msi", "MSOCache", "Windows.old",
            "Common Files", "Internet Explorer", "ModifiableWindowsApps", "Reference Assemblies", "MSBuild",
            "dotnet", "Uninstall Information", "WindowsApps", "WindowsPowerShell", "Application Verifier",
            "Downloads", "Download", "下载", "Documents", "文档", "Desktop", "桌面", "Backup", "Backups", "备份",
            "Temp", "tmp", "Drivers", "Intel", "AMD", "NVIDIA", "Package Cache", "Packages", "Installer",
            "OneDriveTemp", "found.000", "GeekCleaner"
        };

        static void ScanPortable(List<AppEntry> list)
        {
            var known = list.Where(a => a.InstallLocation != null).Select(a => a.InstallLocation).ToList();
            var roots = new List<string>();
            Action<string> addRoot = delegate(string p)
            {
                p = Util.Norm(p);
                if (p != null && Directory.Exists(p) && !roots.Contains(p, StringComparer.OrdinalIgnoreCase)) roots.Add(p);
            };
            addRoot(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
            addRoot(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
            addRoot(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"));
            string[] containers = { "", "Program Files", "Program Files (x86)", "Games", "Game", "游戏", "Software", "软件",
                                    "Tools", "工具", "Apps", @"SteamLibrary\steamapps\common", @"Steam\steamapps\common",
                                    "Epic Games", "WeGameApps" };
            foreach (string d in Util.FixedDrives())
                foreach (string c in containers)
                    addRoot(c == "" ? d : d + c);

            var rootSet = new HashSet<string>(roots, StringComparer.OrdinalIgnoreCase);
            foreach (string root in roots)
            {
                string[] dirs;
                try { dirs = Directory.GetDirectories(root); } catch { continue; }
                foreach (string dir in dirs)
                {
                    try
                    {
                        string name = Path.GetFileName(dir);
                        if (rootSet.Contains(dir) || SkipNames.Contains(name)) continue;
                        if (name.StartsWith("$") || name.StartsWith("Windows", StringComparison.OrdinalIgnoreCase) ||
                            name.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase)) continue;
                        FileAttributes at = File.GetAttributes(dir);
                        if ((at & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) != 0) continue;
                        if (!Util.IsSafePath(dir)) continue;
                        if (known.Any(k => Util.IsUnder(k, dir) || Util.IsUnder(dir, k))) continue;
                        // 是其他容器目录的上级（例如 D:\Steam 包含 steamapps\common）则跳过
                        if (roots.Any(r => Util.IsUnder(r, dir))) continue;
                        string exe = PickExe(dir, name);
                        if (exe != null) { AddPortable(list, known, dir, name, name, exe); continue; }
                        // 顶层没有 exe：多半是厂商文件夹（如 Tencent、EA），把其下每个含 exe 的子文件夹单独列出
                        int n = 0;
                        foreach (string sub in Directory.GetDirectories(dir))
                        {
                            if (++n > 40) break;
                            string sn = Path.GetFileName(sub);
                            if (SkipNames.Contains(sn)) continue;
                            if (known.Any(k => Util.IsUnder(k, sub) || Util.IsUnder(sub, k))) continue;
                            string se = PickExe(sub, sn);
                            if (se != null) AddPortable(list, known, sub, name + " \\ " + sn, sn, se);
                        }
                    }
                    catch { }
                }
            }
        }

        static void AddPortable(List<AppEntry> list, List<string> known, string dir, string display, string key, string exe)
        {
            if (!Util.IsSafePath(dir)) return;
            var a = new AppEntry();
            a.Name = display;
            a.Kind = AppKind.Portable;
            a.InstallLocation = dir;
            a.DisplayIcon = exe;
            a.KeyName = key;
            try
            {
                var vi = FileVersionInfo.GetVersionInfo(exe);
                a.Version = (vi.ProductVersion ?? vi.FileVersion ?? "").Trim();
                a.Publisher = (vi.CompanyName ?? "").Trim();
            }
            catch { }
            try { a.InstallDate = Directory.GetCreationTime(dir).ToString("yyyy-MM-dd"); } catch { }
            list.Add(a);
            known.Add(dir);
        }

        static string PickExe(string dir, string name)
        {
            string[] exes;
            try { exes = Directory.GetFiles(dir, "*.exe"); } catch { return null; }
            string best = null;
            foreach (string e in exes)
            {
                string n = Path.GetFileNameWithoutExtension(e);
                if (n.IndexOf("unins", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (n.IndexOf("setup", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (n.IndexOf("crash", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) return e;
                if (best == null) best = e;
            }
            return best;
        }

        // ---------- 应用商店 (UWP) 应用 ----------

        static void ScanStore(List<AppEntry> list)
        {
            string script =
                "[Console]::OutputEncoding=[Text.Encoding]::UTF8; $m=@{}; " +
                // 用开始菜单里的名称作为友好显示名；没有的话去掉包名前面的发布者前缀
                "try { Get-StartApps | ForEach-Object { $f=($_.AppID -split '!')[0]; if(-not $m.ContainsKey($f)){ $m[$f]=$_.Name } } } catch {}; " +
                "Get-AppxPackage | Where-Object { -not $_.IsFramework -and " +
                "$_.SignatureKind -ne 'System' -and -not $_.NonRemovable -and -not $_.IsResourcePackage } | ForEach-Object { " +
                "$n=$m[$_.PackageFamilyName]; if(-not $n){ $n=($_.Name -replace '^[^.]*\\.','') }; " +
                "($n -replace '\\|',' ') + '|' + $_.PackageFullName + '|' + $_.Version + '|' + $_.Publisher + '|' + $_.InstallLocation }";
            string b64 = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            string output = Util.RunCapture("powershell.exe", "-NoProfile -NonInteractive -EncodedCommand " + b64, Encoding.UTF8);
            foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] p = line.Split('|');
                if (p.Length < 5 || p[0].Length == 0) continue;
                var a = new AppEntry();
                a.Name = p[0];
                a.PackageFullName = p[1];
                a.Version = p[2];
                Match m = Regex.Match(p[3], @"CN=([^,]+)");
                a.Publisher = m.Success ? m.Groups[1].Value : p[3];
                a.InstallLocation = Util.Norm(p[4]);
                a.Kind = AppKind.Store;
                a.KeyName = p[0];
                list.Add(a);
            }
        }
    }
    /// <summary>从系统的启动记录 (UserAssist) 读取每个程序的启动次数和最后一次启动时间。</summary>
    static class Usage
    {
        static string Rot13(string s)
        {
            char[] c = s.ToCharArray();
            for (int i = 0; i < c.Length; i++)
            {
                char ch = c[i];
                if (ch >= 'a' && ch <= 'z') c[i] = (char)('a' + (ch - 'a' + 13) % 26);
                else if (ch >= 'A' && ch <= 'Z') c[i] = (char)('A' + (ch - 'A' + 13) % 26);
            }
            return new string(c);
        }

        public static void Fill(List<AppEntry> apps)
        {
            var known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            known["{6D809377-6AF0-444B-8957-A3773F02200E}"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            known["{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            known["{F38BF404-1D43-42F2-9305-67DE0B28FC23}"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            known["{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}"] = Environment.GetFolderPath(Environment.SpecialFolder.System);
            known["{D65231B0-B2F1-4857-A4CE-A8E7C6EA7D27}"] = Environment.GetFolderPath(Environment.SpecialFolder.SystemX86);

            var entries = new List<Tuple<string, int, DateTime>>();
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist"))
            {
                if (k == null) return;
                foreach (string sub in k.GetSubKeyNames())
                {
                    using (var c = k.OpenSubKey(sub + @"\Count"))
                    {
                        if (c == null) continue;
                        foreach (string name in c.GetValueNames())
                        {
                            byte[] d = c.GetValue(name) as byte[];
                            if (d == null || d.Length < 68) continue;
                            long ft = BitConverter.ToInt64(d, 60);
                            if (ft <= 0) continue;
                            DateTime t;
                            try { t = DateTime.FromFileTime(ft); } catch { continue; }
                            string p = Rot13(name);
                            if (p.StartsWith("{"))
                            {
                                int e = p.IndexOf('}');
                                string basePath;
                                if (e > 0 && known.TryGetValue(p.Substring(0, e + 1), out basePath)) p = basePath + p.Substring(e + 1);
                            }
                            entries.Add(Tuple.Create(p, BitConverter.ToInt32(d, 4), t));
                        }
                    }
                }
            }

            foreach (var a in apps)
            {
                string fam = null;
                if (a.Kind == AppKind.Store && a.PackageFullName != null)
                {
                    string[] parts = a.PackageFullName.Split('_');
                    if (parts.Length >= 2) fam = parts[0] + "_" + parts[parts.Length - 1] + "!";
                }
                else if (a.InstallLocation == null) continue;
                foreach (var e in entries)
                {
                    bool hit = fam != null ? e.Item1.StartsWith(fam, StringComparison.OrdinalIgnoreCase) : Util.IsUnder(e.Item1, a.InstallLocation);
                    if (!hit) continue;
                    a.RunCount += e.Item2;
                    if (e.Item3 > a.LastUsed) a.LastUsed = e.Item3;
                }
            }
        }
    }
}
