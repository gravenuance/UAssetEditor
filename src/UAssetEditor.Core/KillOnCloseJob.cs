using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using UAssetEditor.Core.Logging;

namespace UAssetEditor.Core;

/// <summary>A Windows job object that kills every process in it when its last handle closes. The OS closes a process's handles
/// however it ends, so a host that crashes or is killed takes its pak workers and retoc runs with it instead of leaving them
/// running. Off Windows, or when Windows refuses the job, processes are left as they are and a warning is logged.</summary>
internal sealed partial class KillOnCloseJob : IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    // Made on first use, after the host has set up logging, so a refused job is not warned about into a no-op logger.
    private static readonly Lazy<KillOnCloseJob> ChildProcessJob = new(() => new KillOnCloseJob());

    private readonly SafeJobHandle? _handle;

    /// <summary>The job the child processes of this process join. Never disposed, so it closes only when the process ends.</summary>
    public static KillOnCloseJob ChildProcesses => ChildProcessJob.Value;

    public KillOnCloseJob()
    {
        if (!OperatingSystem.IsWindows()) return;

        var handle = CreateJobObjectW(IntPtr.Zero, null);
        var limits = new ExtendedLimitInformation { BasicLimitInformation = new BasicLimitInformation { LimitFlags = JobObjectLimitKillOnJobClose } };
        if (handle.IsInvalid || !SetInformationJobObject(handle, JobObjectExtendedLimitInformation, ref limits, (uint)Marshal.SizeOf<ExtendedLimitInformation>()))
        {
            LogJobNotCreated(AppLog.For<KillOnCloseJob>(), new Win32Exception(Marshal.GetLastPInvokeError()));
            handle.Dispose();
            return;
        }
        _handle = handle;
    }

    /// <summary>Puts a just-started <paramref name="process"/> in the job, so it ends when the job closes. A host killed in the
    /// moment between the start and this call still leaves that one process behind: <see cref="Process"/> cannot start suspended.</summary>
    public void Assign(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (_handle == null) return;
        if (!AssignProcessToJobObject(_handle, process.SafeHandle) && !process.HasExited)
            LogNotAssigned(AppLog.For<KillOnCloseJob>(), process.Id, new Win32Exception(Marshal.GetLastPInvokeError()));
    }

    /// <summary>Whether <paramref name="process"/> is in this job.</summary>
    public bool Contains(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        return _handle != null && IsProcessInJob(process.SafeHandle, _handle, out var inJob) && inJob;
    }

    public void Dispose() => _handle?.Dispose();

    [LoggerMessage(Level = LogLevel.Warning, Message = "No kill-on-close job could be made, so child processes outlive a host that crashes")]
    private static partial void LogJobNotCreated(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Child process {ProcessId} could not join the kill-on-close job, so it outlives a host that crashes")]
    private static partial void LogNotAssigned(ILogger logger, int processId, Exception exception);

    private sealed class SafeJobHandle() : SafeHandleZeroOrMinusOneIsInvalid(ownsHandle: true)
    {
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeJobHandle CreateJobObjectW(IntPtr jobAttributes, string? name);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeJobHandle job, int infoClass, ref ExtendedLimitInformation info, uint length);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeJobHandle job, SafeProcessHandle process);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(SafeProcessHandle process, SafeJobHandle job, [MarshalAs(UnmanagedType.Bool)] out bool result);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
