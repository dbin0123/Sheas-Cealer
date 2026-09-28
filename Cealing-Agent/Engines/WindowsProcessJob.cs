using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Cealing_Agent.Engines;

/// <summary>
/// Windows Job Object：把引擎子进程绑到 agent 自己身上，agent 一没内核就收掉孩子。
/// </summary>
/// <remarks>
/// 这是唯一**不依赖 agent 配合**的机制：SIGKILL、蓝屏前断电、进程崩溃都不会给 agent
/// 任何清理机会，而 JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE 是内核在句柄关闭时执行的
/// （进程一死，句柄表整个消失，等于句柄被关闭）。所以它专门补上 pid 记录那条「下次启动才清扫」
/// 的时间窗 —— 在那之前孤儿 mihomo 会一直占着 TUN 和端口。
///
/// 非 Windows 平台没有 Job Object，全部走 <see cref="EngineGuard"/> 的 pid 记录。
/// </remarks>
internal static class WindowsProcessJob
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint KillOnJobClose = 0x00002000;

    // 按最小权限打开句柄，别用 Process.Handle —— 它要的是 ALL_ACCESS，遇到受保护进程会直接抛。
    // 前两个是 AssignProcessToJobObject 的要求，第三个只用来查「这个 pid 在不在我的 job 里」。
    private const uint ProcessSetQuota = 0x0100;
    private const uint ProcessTerminate = 0x0001;
    private const uint ProcessQueryLimitedInformation = 0x1000;

    private static readonly object Gate = new();

    // 故意不 Close：句柄活到进程结束，退出那一刻由内核关闭它并连带杀掉 job 里所有进程。
    private static IntPtr _job = IntPtr.Zero;
    private static bool _initAttempted;

    internal static void Assign(int pid)
    {
        if (!OperatingSystem.IsWindows())
            return;

        IntPtr job = EnsureJob();

        if (job == IntPtr.Zero)
            return;

        IntPtr process = IntPtr.Zero;

        try
        {
            process = OpenProcess(ProcessSetQuota | ProcessTerminate, false, (uint)pid);

            if (process == IntPtr.Zero)
            {
                AgentLog.Warn($"cannot open engine pid {pid} for job assignment (Win32 {Marshal.GetLastWin32Error()})");

                return;
            }

            if (!AssignProcessToJobObject(job, process))
                AgentLog.Warn($"assigning engine pid {pid} to the job failed (Win32 {Marshal.GetLastWin32Error()})");
            else
                AgentLog.Info($"engine pid {pid} bound to the agent job (dies with it)");
        }
        finally
        {
            if (process != IntPtr.Zero)
                CloseHandle(process);
        }
    }

    /// <summary>
    /// pid 是否真的被绑进了本进程的 Job（Windows 之外恒为 false）。
    /// 绑定失败只会在日志里留一句 warning，而「warning」在用户那边等于「孤儿引擎还在」，
    /// 所以把它做成可断言的查询，测试和排障都能直接问一句到底绑上了没有。
    /// </summary>
    internal static bool IsMember(int pid)
    {
        if (!OperatingSystem.IsWindows())
            return false;

        IntPtr job = EnsureJob();

        if (job == IntPtr.Zero)
            return false;

        IntPtr process = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);

        if (process == IntPtr.Zero)
            return false;

        try
        {
            return IsProcessInJob(process, job, out bool member) && member;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private static IntPtr EnsureJob()
    {
        lock (Gate)
        {
            if (_initAttempted)
                return _job;

            _initAttempted = true;

            try
            {
                IntPtr job = CreateJobObjectW(IntPtr.Zero, null);

                if (job == IntPtr.Zero)
                {
                    AgentLog.Warn($"CreateJobObject failed (Win32 {Marshal.GetLastWin32Error()}); orphan engines will only be reaped on next start");

                    return IntPtr.Zero;
                }

                JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits = new()
                {
                    BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION { LimitFlags = KillOnJobClose }
                };

                if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref limits, (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
                {
                    AgentLog.Warn($"SetInformationJobObject(KILL_ON_JOB_CLOSE) failed (Win32 {Marshal.GetLastWin32Error()}); orphan engines will only be reaped on next start");
                    CloseHandle(job);

                    return IntPtr.Zero;
                }

                _job = job;
            }
            catch (Exception ex)
            {
                AgentLog.Warn($"job object setup failed: {ex.Message}");
            }

            return _job;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int jobObjectInformationClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION jobObjectInformation, uint jobObjectInformationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public long Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
