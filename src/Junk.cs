using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GeekCleaner
{
    class JunkItem
    {
        public string FsPath;
        public long Size;
        public bool Checked;
        public string RecycleDrive; // 非 null 表示这是某个盘的回收站
        public string CatName = "";
        public DateTime Used = DateTime.MinValue; // 最后修改/访问时间中较新的一个；MinValue 表示未知
        public int Risk, Score, Level;            // Level: 0 优先清理, 1 建议清理, 2 可以清理, 3 自行判断

        public static readonly string[] LevelNames = { "优先清理", "建议清理", "可以清理", "自行判断" };
        static readonly string[] RiskNames = { "可安全删除", "基本安全", "删除前请确认", "重要性需你判断" };

        public int IdleDays { get { return Used == DateTime.MinValue ? -1 : Math.Max(0, (int)(DateTime.Now - Used).TotalDays); } }

        public string IdleText
        {
            get
            {
                int d = IdleDays;
                if (d < 0) return "";
                if (d == 0) return "今天";
                if (d < 60) return d + " 天";
                if (d < 730) return (d / 30) + " 个月";
                return (d / 365) + " 年";
            }
        }

        /// <summary>综合评分 = 重要程度(安全性) 50 + 闲置时间 30 + 占用大小 20。</summary>
        public void Rate()
        {
            int[] safety = { 50, 38, 22, 5 };
            int d = IdleDays;
            int idle = d < 0 ? 15 : d < 1 ? 0 : d < 7 ? 6 : d < 30 ? 12 : d < 90 ? 18 : d < 365 ? 24 : 30;
            int size = Size >= 1L << 30 ? 20 : Size >= 100L << 20 ? 16 : Size >= 10L << 20 ? 12 : Size >= 1L << 20 ? 8 : Size >= 100L << 10 ? 4 : 1;
            Score = safety[Risk] + idle + size;
            Level = Score >= 78 ? 0 : Score >= 60 ? 1 : Score >= 40 ? 2 : 3;
            // 重要程度封顶：无法判断是否重要的文件（如大文件）永远只给“自行判断”，
            // 需要确认的文件（备份、旧安装包等）最高只到“可以清理”，不会被一键勾选误伤。
            if (Risk >= 3) Level = 3;
            else if (Risk == 2 && Level < 2) Level = 2;
        }

        public string Reason
        {
            get
            {
                string r = RiskNames[Risk];
                int d = IdleDays;
                if (d >= 0) r += d == 0 ? " · 今天还在使用" : " · 已闲置 " + IdleText;
                return r + " · " + Util.FormatSize(Size) + " · 评分 " + Score;
            }
        }
    }

    class JunkCategory
    {
        public string Name, Hint;
        public bool DefaultChecked, StopWU, Virtual;
        public int Risk;
        public List<JunkItem> Items = new List<JunkItem>();
        public long Total { get { long t = 0; foreach (var i in Items) t += i.Size; return t; } }
    }

    class JunkResult
    {
        public long Freed;
        public int Deleted;
        public List<string> Failed = new List<string>();
    }

    class JunkScanner
    {
        const long GB = 1024L * 1024 * 1024;

        Action<string> progress;
        CancellationToken ct;
        DateTime now;
        int counter;
        readonly HashSet<string> skipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly List<JunkCategory> cats = new List<JunkCategory>();
        JunkCategory cTmp, cDump, cLog, cBak, cPartial, cBig;

        static readonly HashSet<string> RootSkip = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "Windows", "Program Files", "Program Files (x86)", "$Recycle.Bin", "System Volume Information",
            "Windows.old", "$Windows.~BT", "$Windows.~WS", "Recovery", "Config.Msi", "$WinREAgent", "MSOCache",
            "Documents and Settings", "$SysReset", "$GetCurrent"
        };
        static readonly HashSet<string> AnySkip = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            ".git", "WindowsApps", "WinSxS", "Package Cache"
        };

        JunkCategory Cat(string name, string hint, bool def, int risk)
        {
            var c = new JunkCategory { Name = name, Hint = hint, DefaultChecked = def, Risk = risk };
            cats.Add(c);
            return c;
        }

        void Tick(string what)
        {
            ct.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref counter) % 300 == 0 && progress != null) progress("正在扫描: " + what);
        }

        void AddItem(JunkCategory c, string path, long size, DateTime used)
        {
            var it = new JunkItem { FsPath = path, Size = size, Checked = c.DefaultChecked, Used = used, Risk = c.Risk, CatName = c.Name };
            lock (c) c.Items.Add(it);
        }

        static DateTime Later(FileSystemInfo f)
        {
            try
            {
                DateTime a = f.LastWriteTime, b = f.LastAccessTime;
                return a > b ? a : b;
            }
            catch { return DateTime.MinValue; }
        }

        /// <summary>把目录下的每个直接子项作为垃圾项（目录本身保留）。</summary>
        void AddContents(JunkCategory c, string dir, int minAgeHours, string pattern)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            skipDirs.Add(dir.TrimEnd('\\'));
            try
            {
                foreach (var fsi in new DirectoryInfo(dir).EnumerateFileSystemInfos(pattern ?? "*"))
                {
                    Tick(fsi.FullName);
                    if (minAgeHours > 0 && (now - fsi.LastWriteTime).TotalHours < minAgeHours) continue;
                    var fi = fsi as FileInfo;
                    if (fi != null) AddItem(c, fi.FullName, fi.Length, Later(fi));
                    else if ((fsi.Attributes & FileAttributes.ReparsePoint) == 0)
                        AddItem(c, fsi.FullName, Util.DirSize(fsi.FullName, ct), fsi.LastWriteTime);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { }
        }

        /// <summary>把整个文件或目录作为一个垃圾项。</summary>
        void AddWhole(JunkCategory c, string path)
        {
            try
            {
                if (File.Exists(path)) AddItem(c, path, new FileInfo(path).Length, Later(new FileInfo(path)));
                else if (Directory.Exists(path))
                {
                    skipDirs.Add(path.TrimEnd('\\'));
                    Tick(path);
                    long s = Util.DirSize(path, ct);
                    if (s > 0) AddItem(c, path, s, Directory.GetLastWriteTime(path));
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { }
        }

        public List<JunkCategory> Scan(List<string> driveRoots, Action<string> progress, CancellationToken ct)
        {
            this.progress = progress;
            this.ct = ct;
            now = DateTime.Now;

            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string sysDrive = Path.GetPathRoot(win);
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string prof = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string pdata = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            bool sys = driveRoots.Any(d => string.Equals(d, sysDrive, StringComparison.OrdinalIgnoreCase));

            skipDirs.Add(Util.BackupRoot);
            skipDirs.Add(Path.Combine(pdata, "Microsoft"));

            if (sys)
            {
                var c = Cat("用户临时文件", "Temp 文件夹中超过 24 小时未修改的内容", true, 0);
                AddContents(c, Path.GetTempPath(), 24, null);
                AddContents(c, Path.Combine(local, "Temp"), 24, null);
                c.Items = c.Items.GroupBy(i => i.FsPath, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();

                c = Cat("系统临时文件", "C:\\Windows\\Temp 中超过 24 小时未修改的内容", true, 0);
                AddContents(c, Path.Combine(win, "Temp"), 24, null);

                c = Cat("Windows 更新下载缓存", "已安装完成的更新安装包，可安全删除", true, 0);
                c.StopWU = true;
                AddContents(c, Path.Combine(win, @"SoftwareDistribution\Download"), 0, null);

                c = Cat("传递优化缓存", "Windows 更新 P2P 分发缓存", true, 0);
                AddContents(c, Path.Combine(win, @"ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache"), 0, null);
                AddContents(c, Path.Combine(win, @"SoftwareDistribution\DeliveryOptimization"), 0, null);

                c = Cat("错误报告与崩溃转储", "Windows 错误报告、内存转储文件", true, 0);
                AddContents(c, Path.Combine(local, "CrashDumps"), 0, null);
                AddContents(c, Path.Combine(pdata, @"Microsoft\Windows\WER\ReportArchive"), 0, null);
                AddContents(c, Path.Combine(pdata, @"Microsoft\Windows\WER\ReportQueue"), 0, null);
                AddContents(c, Path.Combine(pdata, @"Microsoft\Windows\WER\Temp"), 0, null);
                AddContents(c, Path.Combine(local, @"Microsoft\Windows\WER\ReportArchive"), 0, null);
                AddContents(c, Path.Combine(local, @"Microsoft\Windows\WER\ReportQueue"), 0, null);
                AddContents(c, Path.Combine(win, "Minidump"), 0, null);
                AddContents(c, Path.Combine(win, "LiveKernelReports"), 0, "*.dmp");
                AddWhole(c, Path.Combine(win, "MEMORY.DMP"));

                c = Cat("缩略图与图标缓存", "资源管理器缩略图缓存，删除后会自动重建", true, 0);
                AddContents(c, Path.Combine(local, @"Microsoft\Windows\Explorer"), 0, "thumbcache_*.db");
                AddContents(c, Path.Combine(local, @"Microsoft\Windows\Explorer"), 0, "iconcache_*.db");

                c = Cat("浏览器缓存", "Chrome / Edge / Firefox 等的网页缓存（不含密码、书签、历史记录）", true, 0);
                string[] chromium = {
                    Path.Combine(local, @"Google\Chrome\User Data"), Path.Combine(local, @"Microsoft\Edge\User Data"),
                    Path.Combine(local, @"BraveSoftware\Brave-Browser\User Data"), Path.Combine(local, @"360ChromeX\Chrome\User Data"),
                    Path.Combine(local, @"Tencent\QQBrowser\User Data")
                };
                foreach (string ud in chromium)
                {
                    if (!Directory.Exists(ud)) continue;
                    try
                    {
                        foreach (string p in Directory.GetDirectories(ud))
                        {
                            string n = Path.GetFileName(p);
                            if (n != "Default" && !n.StartsWith("Profile ")) continue;
                            AddWhole(c, Path.Combine(p, "Cache"));
                            AddWhole(c, Path.Combine(p, "Code Cache"));
                            AddWhole(c, Path.Combine(p, "GPUCache"));
                            AddWhole(c, Path.Combine(p, @"Service Worker\CacheStorage"));
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { }
                }
                string ff = Path.Combine(local, @"Mozilla\Firefox\Profiles");
                if (Directory.Exists(ff))
                {
                    try { foreach (string p in Directory.GetDirectories(ff)) AddWhole(c, Path.Combine(p, "cache2")); }
                    catch (OperationCanceledException) { throw; }
                    catch { }
                }

                c = Cat("Windows 日志文件", "系统安装与更新产生的日志 (Windows\\Logs)", true, 1);
                AddContents(c, Path.Combine(win, @"Logs\CBS"), 24, null);
                AddContents(c, Path.Combine(win, @"Logs\DISM"), 24, null);
                AddContents(c, Path.Combine(win, @"Logs\WindowsUpdate"), 24, null);

                c = Cat("开发工具缓存", "pip / npm / NuGet 下载缓存，删除后需要时会重新下载", false, 1);
                AddWhole(c, Path.Combine(local, @"pip\cache"));
                AddWhole(c, Path.Combine(roaming, "npm-cache"));
                AddWhole(c, Path.Combine(local, @"npm-cache"));
                AddWhole(c, Path.Combine(local, @"NuGet\v3-cache"));

                c = Cat("下载文件夹中的旧安装包", "“下载”里超过 60 天的 exe / msi / iso 安装包，请自行确认", false, 2);
                string dl = Path.Combine(prof, "Downloads");
                AddContentsNoSkip(c, dl, 24 * 60, "*.exe");
                AddContentsNoSkip(c, dl, 24 * 60, "*.msi");
                AddContentsNoSkip(c, dl, 24 * 60, "*.iso");

                c = Cat("旧的 Windows 安装", "系统升级后留下的旧系统文件；删除后无法回退到旧版本", false, 2);
                AddWhole(c, sysDrive + "Windows.old");
                AddWhole(c, sysDrive + "$Windows.~BT");
                AddWhole(c, sysDrive + "$Windows.~WS");
            }

            // 回收站
            var rb = Cat("回收站", "各磁盘回收站中的内容", true, 0);
            foreach (string d in driveRoots)
            {
                try
                {
                    var info = new Native.SHQUERYRBINFO();
                    info.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.SHQUERYRBINFO));
                    if (Native.SHQueryRecycleBin(d, ref info) == 0 && info.i64NumItems > 0)
                        rb.Items.Add(new JunkItem { FsPath = "回收站 (" + d.TrimEnd('\\') + ")  共 " + info.i64NumItems + " 个对象", Size = info.i64Size, Checked = true, RecycleDrive = d, CatName = rb.Name });
                }
                catch { }
            }

            // 全盘遍历
            cTmp = Cat("全盘临时文件", "*.tmp / *.temp / ~$* / Thumbs.db，超过 24 小时未修改", true, 1);
            cDump = Cat("全盘崩溃转储文件", "*.dmp / *.mdmp / *.hdmp 程序崩溃转储", true, 0);
            cPartial = Cat("未完成的下载", "超过 7 天的 *.crdownload / *.part / *.partial", false, 1);
            cLog = Cat("旧日志文件", "超过 30 天未修改的 *.log，请自行确认", false, 1);
            cBak = Cat("备份与旧文件", "超过 30 天未修改的 *.bak / *.old，请自行确认", false, 2);
            cBig = Cat("大文件 (≥1 GB，仅供参考)", "不一定是垃圾，列出来供你判断是否还需要", false, 3);
            try
            {
                Parallel.ForEach(driveRoots, delegate(string d) { Walk(d, 0); });
            }
            catch (AggregateException ae)
            {
                if (ae.Flatten().InnerExceptions.Any(x => x is OperationCanceledException)) throw new OperationCanceledException();
                throw;
            }

            foreach (var c in cats)
            {
                foreach (var it in c.Items) it.Rate();
                c.Items.Sort(ByPriority);
            }
            return cats.Where(c => c.Items.Count > 0).ToList();
        }

        static int ByPriority(JunkItem a, JunkItem b)
        {
            int c = b.Score.CompareTo(a.Score);
            return c != 0 ? c : b.Size.CompareTo(a.Size);
        }

        /// <summary>虚拟分类：把所有项目放在一起按清理优先级排序（与各分类共享同一批对象）。</summary>
        public static JunkCategory Overview(List<JunkCategory> cats)
        {
            var o = new JunkCategory { Name = "综合清理建议", Hint = "全部项目按「重要程度 + 闲置时间 + 大小」评分排序", Virtual = true, DefaultChecked = true };
            foreach (var c in cats) if (!c.Virtual) o.Items.AddRange(c.Items);
            o.Items.Sort(ByPriority);
            return o;
        }

        void AddContentsNoSkip(JunkCategory c, string dir, int minAgeHours, string pattern)
        {
            AddContents(c, dir, minAgeHours, pattern);
            skipDirs.Remove(dir.TrimEnd('\\'));
        }

        void Walk(string dir, int depth)
        {
            Tick(dir);
            var subdirs = new List<string>();
            try
            {
                foreach (var fsi in new DirectoryInfo(dir).EnumerateFileSystemInfos())
                {
                    FileAttributes a = fsi.Attributes;
                    if ((a & FileAttributes.ReparsePoint) != 0) continue;
                    var fi = fsi as FileInfo;
                    if (fi == null)
                    {
                        string n = fsi.Name;
                        if (depth == 0 && RootSkip.Contains(n)) continue;
                        if (AnySkip.Contains(n) || skipDirs.Contains(fsi.FullName)) continue;
                        subdirs.Add(fsi.FullName);
                    }
                    else Classify(fi, a);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { }
            if (depth <= 1) Parallel.ForEach(subdirs, delegate(string s) { Walk(s, depth + 1); });
            else foreach (string s in subdirs) Walk(s, depth + 1);
        }

        void Classify(FileInfo fi, FileAttributes a)
        {
            string name = fi.Name;
            bool thumbs = string.Equals(name, "Thumbs.db", StringComparison.OrdinalIgnoreCase);
            if ((a & FileAttributes.System) != 0 && !thumbs) return;
            string ext = fi.Extension.ToLowerInvariant();
            double ageH = (now - fi.LastWriteTime).TotalHours;
            long len = fi.Length;

            if (ext == ".tmp" || ext == ".temp" || thumbs || name.StartsWith("~$"))
            {
                if (ageH >= 24) AddItem(cTmp, fi.FullName, len, Later(fi));
            }
            else if (ext == ".dmp" || ext == ".mdmp" || ext == ".hdmp")
            {
                if (ageH >= 24) AddItem(cDump, fi.FullName, len, Later(fi));
            }
            else if (ext == ".crdownload" || ext == ".part" || ext == ".partial")
            {
                if (ageH >= 24 * 7) AddItem(cPartial, fi.FullName, len, Later(fi));
            }
            else if (ext == ".log")
            {
                if (ageH >= 24 * 30 && len > 0) AddItem(cLog, fi.FullName, len, Later(fi));
            }
            else if (ext == ".bak" || ext == ".old")
            {
                if (ageH >= 24 * 30) AddItem(cBak, fi.FullName, len, Later(fi));
            }
            else if (len >= GB)
            {
                AddItem(cBig, fi.FullName, len, Later(fi));
            }
        }

        // ================= 清理 =================

        public static JunkResult Clean(List<JunkCategory> cats, Action<string> progress, CancellationToken ct)
        {
            var r = new JunkResult();
            bool wu = cats.Any(c => c.StopWU && c.Items.Any(i => i.Checked));
            if (wu)
            {
                if (progress != null) progress("正在暂停 Windows 更新服务…");
                Util.RunWait("net.exe", "stop wuauserv /y", true);
            }
            try
            {
                int n = 0;
                foreach (var c in cats)
                {
                    var keep = new List<JunkItem>();
                    foreach (var it in c.Items)
                    {
                        if (!it.Checked || ct.IsCancellationRequested) { keep.Add(it); continue; }
                        if (++n % 20 == 0 && progress != null) progress("正在清理: " + it.FsPath);
                        if (it.RecycleDrive != null)
                        {
                            int hr = Native.SHEmptyRecycleBin(IntPtr.Zero, it.RecycleDrive, 7);
                            if (hr == 0) { r.Freed += it.Size; r.Deleted++; }
                            else { r.Failed.Add(it.FsPath); keep.Add(it); }
                            continue;
                        }
                        int before = r.Failed.Count;
                        r.Freed += Util.DeleteTree(it.FsPath, r.Failed, false);
                        if (r.Failed.Count == before) r.Deleted++;
                        else if (File.Exists(it.FsPath) || Directory.Exists(it.FsPath)) keep.Add(it);
                    }
                    c.Items = keep;
                }
            }
            finally
            {
                if (wu) Util.RunWait("net.exe", "start wuauserv", true);
            }
            return r;
        }
    }
}
