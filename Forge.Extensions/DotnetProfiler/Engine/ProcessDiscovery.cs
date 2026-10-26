using System.Diagnostics;
using System.Runtime.InteropServices;

using Microsoft.Diagnostics.NETCore.Client;

namespace Forge.Extensions.DotnetProfiler.Engine;

public static partial class ProcessDiscovery
{
	private const uint QueryLimitedInformation = 0x1000;
	private const int ProcessCommandLineInformation = 60;

	public static List<ProcessInfo> List() {
		List<ProcessInfo> result = [];

		foreach (int pid in DiagnosticsClient.GetPublishedProcesses()) {
			if (pid == Environment.ProcessId) continue;

			string name = $"pid {pid}";

			try {
				using Process process = Process.GetProcessById(pid);
				name = process.ProcessName;
			}
			catch (Exception exception) when (exception is ArgumentException or InvalidOperationException) {
				continue;
			}

			string? commandLine = CommandLine(pid);
			result.Add(new ProcessInfo(pid, FriendlyName(name, commandLine), commandLine));
		}

		result.Sort(static (left, right) => string.CompareOrdinal(left.Name, right.Name));
		return result;
	}

	public static string FriendlyName(string processName, string? commandLine) {
		if (!processName.Equals("dotnet", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(commandLine)) return processName;

		return ManagedAssembly(commandLine) is { } assembly ? $"{assembly}  (dotnet)" : processName;
	}

	public static string? ManagedAssembly(ReadOnlySpan<char> commandLine) {
		int i = 0;

		while (i < commandLine.Length) {
			while (i < commandLine.Length && commandLine[i] == ' ') i++;

			bool quoted = i < commandLine.Length && commandLine[i] == '"';
			int start = quoted ? ++i : i;

			while (i < commandLine.Length && (quoted ? commandLine[i] != '"' : commandLine[i] != ' ')) i++;

			ReadOnlySpan<char> token = commandLine[start..i];

			if (quoted && i < commandLine.Length) i++;

			if (token.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) return Path.GetFileNameWithoutExtension(token).ToString();
		}

		return null;
	}

	public static unsafe string? CommandLine(int pid) {
		nint handle = OpenProcess(QueryLimitedInformation, 0, (uint)pid);

		if (handle == 0) return null;

		try {
			Span<byte> stack = stackalloc byte[2048];
			byte[]? rented = null;
			Span<byte> buffer = stack;

			while (true) {
				fixed (byte* data = buffer) {
					int status = NtQueryInformationProcess(handle, ProcessCommandLineInformation, data, (uint)buffer.Length, out uint needed);

					if (status == 0) {
						UnicodeString* header = (UnicodeString*)data;
						string text = new((char*)header->Buffer, 0, header->Length / 2);

						if (rented is not null) System.Buffers.ArrayPool<byte>.Shared.Return(rented);

						return text;
					}

					if (needed <= buffer.Length || rented is not null) return null;

					rented = System.Buffers.ArrayPool<byte>.Shared.Rent((int)needed);
					buffer = rented;
				}
			}
		}
		finally {
			CloseHandle(handle);
		}
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct UnicodeString
	{
		public ushort Length;
		public ushort MaximumLength;
		public nint Buffer;
	}

	[LibraryImport("kernel32.dll")]
	private static partial nint OpenProcess(uint access, int inherit, uint pid);

	[LibraryImport("kernel32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool CloseHandle(nint handle);

	[LibraryImport("ntdll.dll")]
	private static unsafe partial int NtQueryInformationProcess(nint process, int informationClass, byte* information, uint length, out uint returned);
}
