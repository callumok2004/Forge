using System.Runtime.InteropServices;
using System.Text;

namespace Forge.Extensions.GLua;

public static unsafe class LuaShared
{
	private static readonly Lock Sync = new();
	private static nint _module;
	private static delegate* unmanaged[Cdecl]<nint> _newState;
	private static delegate* unmanaged[Cdecl]<nint, void> _close;
	private static delegate* unmanaged[Cdecl]<nint, byte*, nuint, byte*, int> _loadBuffer;
	private static delegate* unmanaged[Cdecl]<nint, delegate* unmanaged[Cdecl]<nint, void*, nuint, void*, int>, void*, int> _dump;
	private static delegate* unmanaged[Cdecl]<nint, int, nuint*, byte*> _toString;

	public static string Directory => Path.Combine(GLuaExtension.Directory, "lib");

	private static void Load() {
		if (_module != 0) return;

		NativeLibrary.Load(Path.Combine(Directory, "tier0.dll"));
		NativeLibrary.Load(Path.Combine(Directory, "vstdlib.dll"));
		nint module = NativeLibrary.Load(Path.Combine(Directory, "lua_shared.dll"));

		_newState = (delegate* unmanaged[Cdecl]<nint>)NativeLibrary.GetExport(module, "luaL_newstate");
		_close = (delegate* unmanaged[Cdecl]<nint, void>)NativeLibrary.GetExport(module, "lua_close");
		_loadBuffer = (delegate* unmanaged[Cdecl]<nint, byte*, nuint, byte*, int>)NativeLibrary.GetExport(module, "luaL_loadbuffer");
		_dump = (delegate* unmanaged[Cdecl]<nint, delegate* unmanaged[Cdecl]<nint, void*, nuint, void*, int>, void*, int>)NativeLibrary.GetExport(module, "lua_dump");
		_toString = (delegate* unmanaged[Cdecl]<nint, int, nuint*, byte*>)NativeLibrary.GetExport(module, "lua_tolstring");
		_module = module;
	}

	[UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
	private static int Write(nint state, void* data, nuint size, void* user) {
		MemoryStream output = (MemoryStream)GCHandle.FromIntPtr((nint)user).Target!;
		output.Write(new ReadOnlySpan<byte>(data, checked((int)size)));
		return 0;
	}

	public static byte[] Compile(string source, string chunkName) {
		lock (Sync) {
			Load();

			byte[] code = Encoding.UTF8.GetBytes(source);
			byte[] name = Encoding.UTF8.GetBytes("@" + chunkName + "\0");
			nint state = _newState();

			if (state == 0) throw new InvalidOperationException("could not create a Lua state");

			try {
				int status;

				fixed (byte* codePointer = code)
				fixed (byte* namePointer = name) status = _loadBuffer(state, codePointer, (nuint)code.Length, namePointer);

				if (status != 0) {
					nuint length;
					byte* message = _toString(state, -1, &length);
					throw new InvalidDataException(message is null ? "failed to load chunk" : Encoding.UTF8.GetString(message, checked((int)length)));
				}

				using MemoryStream output = new();
				GCHandle handle = GCHandle.Alloc(output);

				try {
					if (_dump(state, &Write, (void*)GCHandle.ToIntPtr(handle)) != 0) throw new InvalidDataException("failed to dump bytecode");
				}
				finally {
					handle.Free();
				}

				return output.ToArray();
			}
			finally {
				_close(state);
			}
		}
	}
}
