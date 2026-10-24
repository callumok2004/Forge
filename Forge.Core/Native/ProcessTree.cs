using System.Runtime.InteropServices;

namespace Forge.Core.Native;

public readonly record struct ResourceUsage(double CpuPercent, long MemoryBytes);

public readonly record struct ProcessUsage(uint Id, uint ParentId, int Depth, string Name, double CpuPercent, long MemoryBytes);

public sealed partial class ProcessTree
{
	private const uint SnapProcess = 0x2;
	private const uint QueryLimitedInformation = 0x1000;
	private const uint VmRead = 0x0010;

	private readonly uint _root = (uint)Environment.ProcessId;
	private readonly List<(uint Id, uint Parent, int Depth)> _found = [];
	private readonly List<(uint Id, uint Parent)> _all = [];
	private readonly Dictionary<uint, string> _names = [];
	private readonly List<ProcessUsage> _rows = [];
	private readonly Lock _sync = new();

	private Dictionary<uint, long> _cpuTicks = [];
	private Dictionary<uint, long> _previousTicks = [];
	private long _sampledAt;

	public IReadOnlyList<ProcessUsage> Rows {
		get {
			lock (_sync) return [.. _rows];
		}
	}

	public ResourceUsage Sample() {
		long now = System.Diagnostics.Stopwatch.GetTimestamp();
		double elapsedTicks = _sampledAt == 0 ? 0 : (now - _sampledAt) * (10_000_000.0 / System.Diagnostics.Stopwatch.Frequency);
		double scale = elapsedTicks > 0 ? 100.0 / elapsedTicks / Environment.ProcessorCount : 0;

		_sampledAt = now;
		(_previousTicks, _cpuTicks) = (_cpuTicks, _previousTicks);
		_cpuTicks.Clear();

		CollectDescendants();

		long busyTicks = 0;
		long memory = 0;

		lock (_sync) {
			_rows.Clear();

			foreach ((uint id, uint parent, int depth) in _found) {
				nint process = OpenProcess(QueryLimitedInformation | VmRead, false, id);

				if (process == 0) continue;

				long busy = 0;
				long working = 0;

				try {
					if (GetProcessTimes(process, out _, out _, out long kernel, out long user)) {
						long total = kernel + user;
						_cpuTicks[id] = total;

						if (_previousTicks.TryGetValue(id, out long before)) busy = Math.Max(total - before, 0);
					}

					MemoryCounters counters = new() { Size = (uint)Marshal.SizeOf<MemoryCounters>() };

					if (GetProcessMemoryInfo(process, ref counters, counters.Size)) working = (long)counters.PrivateWorkingSetSize;
				}
				finally {
					_ = CloseHandle(process);
				}

				busyTicks += busy;
				memory += working;
				_rows.Add(new ProcessUsage(id, parent, depth, _names.GetValueOrDefault(id, "?"), Math.Clamp(busy * scale, 0, 100), working));
			}
		}

		return new ResourceUsage(Math.Clamp(busyTicks * scale, 0, 100), memory);
	}

	private unsafe void CollectDescendants() {
		_found.Clear();
		_all.Clear();
		_found.Add((_root, 0, 0));

		nint snapshot = CreateToolhelp32Snapshot(SnapProcess, 0);

		if (snapshot is 0 or -1) return;

		try {
			ProcessEntry entry = new() { Size = (uint)sizeof(ProcessEntry) };

			for (bool more = Process32FirstW(snapshot, ref entry); more; more = Process32NextW(snapshot, ref entry)) {
				_all.Add((entry.ProcessId, entry.ParentProcessId));

				if (!_names.ContainsKey(entry.ProcessId)) _names[entry.ProcessId] = new string(entry.ExeFile, 0, IndexOfNull(entry.ExeFile, 260));
			}
		}
		finally {
			_ = CloseHandle(snapshot);
		}

		for (int i = 0; i < _found.Count; i++) {
			(uint current, _, int depth) = _found[i];

			foreach ((uint id, uint parent) in _all)
				if (parent == current && id != current && !Contains(id)) _found.Insert(i + 1, (id, parent, depth + 1));
		}
	}

	private bool Contains(uint id) {
		foreach ((uint existing, _, _) in _found)
			if (existing == id) return true;

		return false;
	}

	private static unsafe int IndexOfNull(char* text, int length) {
		for (int i = 0; i < length; i++)
			if (text[i] == '\0') return i;

		return length;
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
