using System.Runtime.InteropServices;
using System.Text;

namespace Forge.Terminal;

public sealed unsafe partial class ConPtyHost : IDisposable
{
	private const string Kernel32 = "kernel32.dll";

	private const uint ExtendedStartupInfoPresent = 0x00080000;
	private const nint PseudoConsoleAttribute = 0x00020016;

	private const int ReadBufferSize = 8192;

	private nint _input;
	private nint _output;
	private nint _writeToChild;
	private nint _readFromChild;
	private nint _console;
	private nint _process;
	private nint _thread;
	private nint _attributes;

	private Thread? _reader;
	private Thread? _waiter;
	private volatile bool _closing;
	private volatile bool _exited;

	public event Action<string>? Output;

	public event Action? Exited;

	public bool IsRunning => _process != 0 && !_closing;

	public int ExitCode { get; private set; }

	public bool Start(string command, string workingDirectory, int columns, int rows) {
		if (!CreatePipe(out _readFromChild, out _output, 0, 0) ||
			!CreatePipe(out _input, out _writeToChild, 0, 0)) return false;

		if (CreatePseudoConsole(Pack(columns, rows), _input, _output, 0, out _console) != 0) return false;

		if (!StartProcess(command, workingDirectory)) return false;

		CloseAndClear(ref _input);
		CloseAndClear(ref _output);

		_reader = new Thread(ReadLoop) {
			IsBackground = true,
			Name = "Forge terminal reader",
		};

		_reader.Start();

		_waiter = new Thread(WaitLoop) {
			IsBackground = true,
			Name = "Forge terminal waiter",
		};

		_waiter.Start();
		return true;
	}

	private void WaitLoop() {
		nint process = _process;

		if (process == 0) return;

		WaitForSingleObject(process, uint.MaxValue);

		uint code;

		if (GetExitCodeProcess(process, &code)) ExitCode = unchecked((int)code);

		if (_exited) return;

		_exited = true;

		if (!_closing) Exited?.Invoke();
	}

	private bool StartProcess(string command, string workingDirectory) {
		nuint size = 0;
		InitializeProcThreadAttributeList(0, 1, 0, &size);

		_attributes = Marshal.AllocHGlobal((nint)size);

		if (!InitializeProcThreadAttributeList(_attributes, 1, 0, &size) ||
			!UpdateProcThreadAttribute(_attributes, 0, PseudoConsoleAttribute, _console,
				(nuint)sizeof(nint), 0, null)) return false;

		StartupInfoEx startup = default;
		startup.Startup.Size = sizeof(StartupInfoEx);
		startup.AttributeList = _attributes;

		char[] mutable = new char[command.Length + 1];
		command.CopyTo(mutable);

		fixed (char* line = mutable)
		fixed (char* directory = workingDirectory) {
			ProcessInformation information = default;

			bool created = CreateProcess(null, line, 0, 0, false, ExtendedStartupInfoPresent, 0,
				directory, &startup, &information);

			if (!created) return false;

			_process = information.Process;
			_thread = information.Thread;
			return true;
		}
	}

	public void Write(ReadOnlySpan<char> text) {
		if (_writeToChild == 0 || text.IsEmpty) return;

		int count = Encoding.UTF8.GetByteCount(text);
		Span<byte> bytes = count <= 256 ? stackalloc byte[count] : new byte[count];
		Encoding.UTF8.GetBytes(text, bytes);

		fixed (byte* pointer = bytes) {
			uint written = 0;
			WriteFile(_writeToChild, pointer, (uint)bytes.Length, &written, 0);
		}
	}

	public void Resize(int columns, int rows) {
		if (_console != 0 && columns > 0 && rows > 0) ResizePseudoConsole(_console, Pack(columns, rows));
	}

	private void ReadLoop() {
		byte[] buffer = new byte[ReadBufferSize];

		Decoder decoder = Encoding.UTF8.GetDecoder();
		char[] characters = new char[ReadBufferSize];

		fixed (byte* pointer = buffer) {
			while (!_closing) {
				uint read = 0;

				if (!ReadFile(_readFromChild, pointer, (uint)buffer.Length, &read, 0) || read == 0) break;

				int decoded = decoder.GetChars(buffer, 0, (int)read, characters, 0);

				if (decoded > 0) Output?.Invoke(new string(characters, 0, decoded));
			}
		}

		if (_process != 0) {
			uint code;
			if (GetExitCodeProcess(_process, &code)) ExitCode = unchecked((int)code);
		}

		if (_exited) return;

		_exited = true;

		if (!_closing) Exited?.Invoke();
	}

	[LibraryImport(Kernel32, SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool GetExitCodeProcess(nint process, uint* exitCode);

	[LibraryImport(Kernel32, SetLastError = true)]
	private static partial uint WaitForSingleObject(nint handle, uint milliseconds);

	private static int Pack(int columns, int rows) =>
		(ushort)Math.Clamp(columns, 1, short.MaxValue) |
		((ushort)Math.Clamp(rows, 1, short.MaxValue) << 16);

	private static void CloseAndClear(ref nint handle) {
		if (handle != 0) {
			CloseHandle(handle);
			handle = 0;
		}
	}

	public void Dispose() {
		_closing = true;

		if (_console != 0) {
			ClosePseudoConsole(_console);
			_console = 0;
		}

		CloseAndClear(ref _writeToChild);
		CloseAndClear(ref _readFromChild);
		CloseAndClear(ref _input);
		CloseAndClear(ref _output);
		CloseAndClear(ref _thread);
		CloseAndClear(ref _process);

		if (_attributes != 0) {
			DeleteProcThreadAttributeList(_attributes);
			Marshal.FreeHGlobal(_attributes);
			_attributes = 0;
		}

		_reader?.Join(250);
		_reader = null;
		_waiter?.Join(250);
		_waiter = null;
	}

	#region Interop

	[StructLayout(LayoutKind.Sequential)]
	private struct StartupInfo
	{
		public int Size;
		public char* Reserved;
		public char* Desktop;
		public char* Title;
		public int X;
		public int Y;
		public int XSize;
		public int YSize;
		public int XCountChars;
		public int YCountChars;
		public int FillAttribute;
		public int Flags;
		public ushort ShowWindow;
		public ushort Reserved2Size;
		public byte* Reserved2;
		public nint StdInput;
		public nint StdOutput;
		public nint StdError;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct StartupInfoEx
	{
		public StartupInfo Startup;
		public nint AttributeList;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct ProcessInformation
	{
		public nint Process;
		public nint Thread;
		public int ProcessId;
		public int ThreadId;
	}

	[LibraryImport(Kernel32, SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool CreatePipe(out nint read, out nint write, nint attributes, uint size);

	[LibraryImport(Kernel32)]
	private static partial int CreatePseudoConsole(int size, nint input, nint output, uint flags,
		out nint console);

	[LibraryImport(Kernel32)]
	private static partial int ResizePseudoConsole(nint console, int size);

	[LibraryImport(Kernel32)]
	private static partial void ClosePseudoConsole(nint console);

	[LibraryImport(Kernel32, SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool InitializeProcThreadAttributeList(nint list, int count, uint flags,
		nuint* size);

	[LibraryImport(Kernel32, SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool UpdateProcThreadAttribute(nint list, uint flags, nint attribute,
		nint value, nuint size, nint previous, nuint* returnSize);

	[LibraryImport(Kernel32)]
	private static partial void DeleteProcThreadAttributeList(nint list);

	[LibraryImport(Kernel32, EntryPoint = "CreateProcessW", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool CreateProcess(char* application, char* commandLine,
		nint processAttributes, nint threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inherit,
		uint flags, nint environment, char* directory, StartupInfoEx* startup,
		ProcessInformation* information);

	[LibraryImport(Kernel32, SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool ReadFile(nint file, byte* buffer, uint count, uint* read, nint overlapped);

	[LibraryImport(Kernel32, SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool WriteFile(nint file, byte* buffer, uint count, uint* written,
		nint overlapped);

	[LibraryImport(Kernel32)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool CloseHandle(nint handle);

	#endregion
}
