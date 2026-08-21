using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Forge.Core.Native;

public static partial class ProcessGroup
{
	private const int ExtendedLimitInformation = 9;
	private const uint LimitKillOnJobClose = 0x2000;

	private static readonly nint Job = Create();

	public static bool IsActive => Job != 0;

	public static Process? Start(ProcessStartInfo startInfo) {
		Process? process = Process.Start(startInfo);

		if (process is not null) Adopt(process);

		return process;
	}

	public static void Adopt(Process process) {
		if (Job == 0) return;

		try {
			_ = AssignProcessToJobObject(Job, process.Handle);
		}
		catch (InvalidOperationException) {
		}
	}

	private static nint Create() {
		nint job = CreateJobObjectW(0, null);

		if (job == 0) return 0;

		JobObjectExtendedLimitInformation limits = default;
		limits.BasicLimitInformation.LimitFlags = LimitKillOnJobClose;

		unsafe {
			if (!SetInformationJobObject(job, ExtendedLimitInformation, &limits, (uint)sizeof(JobObjectExtendedLimitInformation))) {
				_ = CloseHandle(job);
				return 0;
			}
		}

		return job;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct JobObjectBasicLimitInformation
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
	private struct JobObjectExtendedLimitInformation
	{
		public JobObjectBasicLimitInformation BasicLimitInformation;
		public IoCounters IoInfo;
		public nuint ProcessMemoryLimit;
		public nuint JobMemoryLimit;
		public nuint PeakProcessMemoryUsed;
		public nuint PeakJobMemoryUsed;
	}

	[LibraryImport(Win32.Kernel32, EntryPoint = "CloseHandle", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool CloseHandle(nint handle);

	[LibraryImport(Win32.Kernel32, EntryPoint = "CreateJobObjectW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
	private static partial nint CreateJobObjectW(nint attributes, string? name);

	[LibraryImport(Win32.Kernel32, EntryPoint = "AssignProcessToJobObject", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool AssignProcessToJobObject(nint job, nint process);

	[LibraryImport(Win32.Kernel32, EntryPoint = "SetInformationJobObject", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static unsafe partial bool SetInformationJobObject(nint job, int infoClass, void* info, uint length);
}
