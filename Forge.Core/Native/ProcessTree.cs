using System.Runtime.InteropServices;

namespace Forge.Core.Native;

public readonly record struct ResourceUsage(double CpuPercent, long MemoryBytes);

public sealed partial class ProcessTree
{
	private const uint SnapProcess = 0x2;
	private const uint QueryLimitedInformation = 0x1000;
	private const uint VmRead = 0x0010;

	private readonly uint _root = (uint)Environment.ProcessId;
	private readonly Dictionary<uint, long> _cpuTicks = [];
	private long _sampledAt;

	public ResourceUsage Sample() {
		long now = System.Diagnostics.Stopwatch.GetTimestamp();
		double elapsedTicks = _sampledAt == 0 ? 0 : (now - _sampledAt) * (10_000_000.0 / System.Diagnostics.Stopwatch.Frequency);

		_sampledAt = now;

		long busyTicks = 0;
		long memory = 0;
		Dictionary<uint, long> previous = new(_cpuTicks);

		_cpuTicks.Clear();

		foreach (uint id in Descendants()) {
			nint process = OpenProcess(QueryLimitedInformation | VmRead, false, id);

			if (process == 0) continue;

			try {
				if (GetProcessTimes(process, out _, out _, out long kernel, out long user)) {
					long total = kernel + user;
					_cpuTicks[id] = total;

					if (previous.TryGetValue(id, out long before)) busyTicks += Math.Max(total - before, 0);
				}

				MemoryCounters counters = new() { Size = (uint)Marshal.SizeOf<MemoryCounters>() };

				if (GetProcessMemoryInfo(process, ref counters, counters.Size)) memory += (long)counters.PrivateWorkingSetSize;
			}
			finally {
				_ = CloseHandle(process);
			}
		}

		double cpu = elapsedTicks > 0 ? busyTicks / elapsedTicks / Environment.ProcessorCount * 100.0 : 0;

		return new ResourceUsage(Math.Clamp(cpu, 0, 100), memory);
	}

	private List<uint> Descendants() {
		List<uint> found = [_root];
		nint snapshot = CreateToolhelp32Snapshot(SnapProcess, 0);

		if (snapshot is 0 or -1) return found;

		try {
			List<(uint Id, uint Parent)> all = [];
			ProcessEntry entry = new() { Size = (uint)Marshal.SizeOf<ProcessEntry>() };

			for (bool more = Process32FirstW(snapshot, ref entry); more; more = Process32NextW(snapshot, ref entry)) {
				all.Add((entry.ProcessId, entry.ParentProcessId));
			}

			for (int i = 0; i < found.Count; i++) {
				foreach ((uint id, uint parent) in all) {
					if (parent == found[i] && id != found[i] && !found.Contains(id)) found.Add(id);
				}
			}
		}
		finally {
			_ = CloseHandle(snapshot);
		}

		return found;
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private unsafe struct ProcessEntry
	{
		public uint Size;
		public uint Usage;
		public uint ProcessId;
		public nuint DefaultHeapId;
		public uint ModuleId;
		public uint Threads;
		public uint ParentProcessId;
		public int PriorityClassBase;
		public uint Flags;
		public fixed char ExeFile[260];
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct MemoryCounters
	{
		public uint Size;
		public uint PageFaultCount;
		public nuint PeakWorkingSetSize;
		public nuint WorkingSetSize;
		public nuint QuotaPeakPagedPoolUsage;
		public nuint QuotaPagedPoolUsage;
		public nuint QuotaPeakNonPagedPoolUsage;
		public nuint QuotaNonPagedPoolUsage;
		public nuint PagefileUsage;
		public nuint PeakPagefileUsage;
		public nuint PrivateUsage;
		public nuint PrivateWorkingSetSize;
		public ulong SharedCommitUsage;
	}

	[LibraryImport(Win32.Kernel32, SetLastError = true)]
	private static partial nint CreateToolhelp32Snapshot(uint flags, uint processId);

	[LibraryImport(Win32.Kernel32, SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool Process32FirstW(nint snapshot, ref ProcessEntry entry);

	[LibraryImport(Win32.Kernel32, SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool Process32NextW(nint snapshot, ref ProcessEntry entry);

	[LibraryImport(Win32.Kernel32, SetLastError = true)]
	private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

	[LibraryImport(Win32.Kernel32, SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool GetProcessTimes(nint process, out long creation, out long exit, out long kernel, out long user);

	[LibraryImport(Win32.Kernel32, EntryPoint = "K32GetProcessMemoryInfo", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool GetProcessMemoryInfo(nint process, ref MemoryCounters counters, uint size);

	[LibraryImport(Win32.Kernel32, SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool CloseHandle(nint handle);
}
