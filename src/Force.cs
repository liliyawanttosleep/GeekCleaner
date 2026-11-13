using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace GeekCleaner
{
    /// <summary>
    /// 处理“顽固”文件/服务/注册表项：进程被占用、文件夹设了拒绝 ACL、看门狗进程自愈等。
    /// 所有操作都只作用于用户在确认清单里勾选的目标（调用方已用 Util.IsSafePath 把系统目录挡在外面）。
    /// </summary>
    static class Force
    {
        // ---- 启用管理员本就拥有、但默认未激活的特权 ----
        [StructLayout(LayoutKind.Sequential)] struct LUID { public uint Low; public int High; }
        [StructLayout(LayoutKind.Sequential)] struct TOKEN_PRIVILEGES { public uint Count; public LUID Luid; public uint Attributes; }

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool LookupPrivilegeValue(string host, string name, out LUID luid);
        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES state, uint len, IntPtr prev, IntPtr prevLen);
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

        static bool enabled;

        /// <summary>程序启动时调用一次：激活取得所有权、还原、备份、调试等特权。</summary>
        public static void EnablePrivileges()
        {
            if (enabled) return;
            enabled = true;
            foreach (string p in new[] { "SeTakeOwnershipPrivilege", "SeRestorePrivilege", "SeBackupPrivilege", "SeDebugPrivilege", "SeSecurityPrivilege" })
                Enable(p);
        }

        static void Enable(string name)
        {
            IntPtr token = IntPtr.Zero;
            try
            {
                if (!OpenProcessToken(GetCurrentProcess(), 0x28 /* ADJUST|QUERY */, out token)) return;
                LUID luid;
                if (!LookupPrivilegeValue(null, name, out luid)) return;
                var tp = new TOKEN_PRIVILEGES { Count = 1, Luid = luid, Attributes = 2 /* SE_PRIVILEGE_ENABLED */ };
                AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
            }
            catch { }
            finally { if (token != IntPtr.Zero) CloseHandle(token); }
        }

        static readonly SecurityIdentifier Admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        static readonly SecurityIdentifier World = new SecurityIdentifier(WellKnownSidType.WorldSid, null);

        /// <summary>夺取文件/文件夹所有权并授予完全控制，清掉拒绝型 ACE。尽力而为，不抛异常。</summary>
        public static void GrantFull(string path, bool isDir)
        {
            try
            {
                // 第一步：把所有者改成 Administrators（需要 SeTakeOwnership/SeRestore）
                if (isDir)
                {
                    var own = new DirectorySecurity();
                    own.SetOwner(Admins);
                    Directory.SetAccessControl(path, own);
                }
                else
                {
                    var own = new FileSecurity();
                    own.SetOwner(Admins);
                    File.SetAccessControl(path, own);
                }
            }
            catch { }
            try
            {
                // 第二步：成为所有者后，断开继承、删掉旧规则（含“拒绝”），只留下完全控制
                if (isDir)
                {
                    var sec = new DirectorySecurity(path, AccessControlSections.Access);
                    sec.SetAccessRuleProtection(true, false);
                    foreach (FileSystemAccessRule rule in sec.GetAccessRules(true, false, typeof(SecurityIdentifier)))
                        sec.RemoveAccessRule(rule);
                    sec.AddAccessRule(new FileSystemAccessRule(Admins, FileSystemRights.FullControl,
                        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                    sec.AddAccessRule(new FileSystemAccessRule(World, FileSystemRights.FullControl,
                        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                    Directory.SetAccessControl(path, sec);
                }
                else
                {
                    var sec = new FileSecurity(path, AccessControlSections.Access);
                    sec.SetAccessRuleProtection(true, false);
                    foreach (FileSystemAccessRule rule in sec.GetAccessRules(true, false, typeof(SecurityIdentifier)))
                        sec.RemoveAccessRule(rule);
                    sec.AddAccessRule(new FileSystemAccessRule(Admins, FileSystemRights.FullControl, AccessControlType.Allow));
                    sec.AddAccessRule(new FileSystemAccessRule(World, FileSystemRights.FullControl, AccessControlType.Allow));
                    File.SetAccessControl(path, sec);
                }
            }
            catch { }
        }

        // ---- 进程 ----

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool TerminateProcess(IntPtr h, uint code);

        static bool KillPid(int pid)
        {
            try { using (var p = Process.GetProcessById(pid)) { p.Kill(); p.WaitForExit(2000); return true; } }
            catch { }
            IntPtr h = OpenProcess(0x0001 /* TERMINATE */, false, pid);
            if (h == IntPtr.Zero) return false;
            try { return TerminateProcess(h, 1); }
            finally { CloseHandle(h); }
        }

        static string ProcPath(Process p)
        {
            try { return p.MainModule.FileName; } catch { }
            // MainModule 对 32 位/受限进程可能失败，改用 QueryFullProcessImageName
            try
            {
                IntPtr h = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, p.Id);
                if (h == IntPtr.Zero) return null;
                try
                {
                    var sb = new System.Text.StringBuilder(1024);
                    int n = sb.Capacity;
                    return QueryFullProcessImageName(h, 0, sb, ref n) ? sb.ToString() : null;
                }
                finally { CloseHandle(h); }
            }
            catch { return null; }
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool QueryFullProcessImageName(IntPtr h, int flags, System.Text.StringBuilder name, ref int size);

        /// <summary>反复杀掉安装目录下运行的所有进程，压制互相拉起的看门狗。返回是否还有残留。</summary>
        public static void KillUnder(string dir, int passes)
        {
            int me = Process.GetCurrentProcess().Id;
            for (int pass = 0; pass < passes; pass++)
            {
                bool killedAny = false;
                foreach (var p in Process.GetProcesses())
                {
                    try
                    {
                        if (p.Id == me || p.Id <= 4) continue;
                        string f = ProcPath(p);
                        if (f != null && Util.IsUnder(f, dir)) { if (KillPid(p.Id)) killedAny = true; }
                    }
                    catch { }
                    finally { p.Dispose(); }
                }
                if (!killedAny && pass > 0) break; // 连续两轮都没东西可杀就停
                Thread.Sleep(120);
            }
        }

        // ---- 服务 ----

        public static bool ServiceExists(string name)
        {
            return Util.RunWait("sc.exe", "query \"" + name + "\"", true) == 0;
        }

        /// <summary>停止服务；停不下来就按服务的 PID 结束其进程。</summary>
        public static void StopService(string name)
        {
            Util.RunWait("sc.exe", "config \"" + name + "\" start= disabled", true);
            Util.RunWait("sc.exe", "stop \"" + name + "\"", true);
            for (int i = 0; i < 3; i++)
            {
                string q = Util.RunCapture("sc.exe", "queryex \"" + name + "\"", System.Text.Encoding.Default);
                Match m = Regex.Match(q, @"PID\s*:\s*(\d+)");
                if (!m.Success) return;
                int pid = int.Parse(m.Groups[1].Value);
                if (pid <= 4) return;              // 0 = 已停止
                KillPid(pid);
                Thread.Sleep(150);
            }
        }

        // ---- 注册表 ----

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        static extern int RegOpenKeyEx(IntPtr root, string sub, int opts, int rights, out IntPtr key);
        [DllImport("advapi32.dll")] static extern int RegCloseKey(IntPtr key);

        static readonly IntPtr HKLM = new IntPtr(unchecked((int)0x80000002));
        static readonly IntPtr HKCU = new IntPtr(unchecked((int)0x80000001));

        /// <summary>夺取注册表项（及其子项）的所有权并授予完全控制，供删除失败后重试。</summary>
        public static void GrantKey(RegistryKey baseKey, bool hklm, string path)
        {
            // WRITE_OWNER=0x80000, 带上 WOW64_64KEY=0x0100
            IntPtr hk;
            if (RegOpenKeyEx(hklm ? HKLM : HKCU, path, 0, unchecked((int)0x80000) | 0x0100, out hk) != 0) return;
            RegCloseKey(hk);
            try
            {
                using (var k = baseKey.OpenSubKey(path, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.TakeOwnership))
                {
                    if (k == null) return;
                    var sec = k.GetAccessControl(AccessControlSections.Owner);
                    sec.SetOwner(Admins);
                    k.SetAccessControl(sec);
                }
            }
            catch { }
            try
            {
                using (var k = baseKey.OpenSubKey(path, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.ChangePermissions))
                {
                    if (k == null) return;
                    var sec = k.GetAccessControl(AccessControlSections.Access);
                    sec.SetAccessRuleProtection(true, false);
                    foreach (RegistryAccessRule rule in sec.GetAccessRules(true, false, typeof(SecurityIdentifier)))
                        sec.RemoveAccessRule(rule);
                    sec.AddAccessRule(new RegistryAccessRule(Admins, RegistryRights.FullControl,
                        InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow));
                    k.SetAccessControl(sec);
                }
            }
            catch { }
        }
    }
}
