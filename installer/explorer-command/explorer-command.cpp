#include <windows.h>
#include <shobjidl_core.h>
#include <shlwapi.h>
#include <strsafe.h>
#include <new>

static HMODULE g_module = nullptr;
static LONG g_refCount = 0;
static CLSID g_clsid;

static bool ResolveExePath(wchar_t* out, size_t cch) {
	wchar_t directory[MAX_PATH];
	if (GetModuleFileNameW(g_module, directory, ARRAYSIZE(directory)) == 0) return false;

	PathRemoveFileSpecW(directory);

	for (int depth = 0; depth < 3; ++depth) {
		wchar_t candidate[MAX_PATH];
		if (PathCombineW(candidate, directory, EXPLORER_COMMAND_EXE L".exe") && PathFileExistsW(candidate)) {
			StringCchCopyW(out, cch, candidate);
			return true;
		}

		if (!PathRemoveFileSpecW(directory)) break;
	}

	return false;
}

class ExplorerCommand : public IExplorerCommand {
public:
	ExplorerCommand() : m_ref(1) { InterlockedIncrement(&g_refCount); }
	virtual ~ExplorerCommand() { InterlockedDecrement(&g_refCount); }

	IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override {
		if (!ppv) return E_POINTER;

		if (riid == IID_IUnknown || riid == IID_IExplorerCommand) {
			*ppv = static_cast<IExplorerCommand*>(this);
			AddRef();
			return S_OK;
		}

		*ppv = nullptr;
		return E_NOINTERFACE;
	}

	IFACEMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&m_ref); }

	IFACEMETHODIMP_(ULONG) Release() override {
		LONG ref = InterlockedDecrement(&m_ref);
		if (ref == 0) delete this;
		return ref;
	}

	IFACEMETHODIMP GetTitle(IShellItemArray*, LPWSTR* name) override { return SHStrDupW(EXPLORER_COMMAND_TITLE, name); }

	IFACEMETHODIMP GetIcon(IShellItemArray*, LPWSTR* icon) override {
		wchar_t exePath[MAX_PATH];
		if (!ResolveExePath(exePath, ARRAYSIZE(exePath))) {
			*icon = nullptr;
			return E_FAIL;
		}

		return SHStrDupW(exePath, icon);
	}

	IFACEMETHODIMP GetToolTip(IShellItemArray*, LPWSTR* infoTip) override {
		*infoTip = nullptr;
		return E_NOTIMPL;
	}

	IFACEMETHODIMP GetCanonicalName(GUID* guid) override {
		*guid = g_clsid;
		return S_OK;
	}

	IFACEMETHODIMP GetState(IShellItemArray*, BOOL, EXPCMDSTATE* state) override {
		*state = ECS_ENABLED;
		return S_OK;
	}

	IFACEMETHODIMP GetFlags(EXPCMDFLAGS* flags) override {
		*flags = ECF_DEFAULT;
		return S_OK;
	}

	IFACEMETHODIMP EnumSubCommands(IEnumExplorerCommand** enumCommands) override {
		*enumCommands = nullptr;
		return E_NOTIMPL;
	}

	IFACEMETHODIMP Invoke(IShellItemArray* items, IBindCtx*) override {
		wchar_t exePath[MAX_PATH];
		if (!ResolveExePath(exePath, ARRAYSIZE(exePath))) return E_FAIL;

		DWORD count = 0;
		if (items) items->GetCount(&count);

		if (count == 0) {
			Launch(exePath, nullptr);
			return S_OK;
		}

		for (DWORD i = 0; i < count; ++i) {
			IShellItem* item = nullptr;
			if (FAILED(items->GetItemAt(i, &item)) || !item) continue;

			LPWSTR path = nullptr;
			if (SUCCEEDED(item->GetDisplayName(SIGDN_FILESYSPATH, &path)) && path) {
				Launch(exePath, path);
				CoTaskMemFree(path);
			}

			item->Release();
		}

		return S_OK;
	}

private:
	static void Launch(const wchar_t* exePath, const wchar_t* target) {
		wchar_t args[MAX_PATH * 3];
		if (target) StringCchPrintfW(args, ARRAYSIZE(args), L"\"%s\" \"%s\"", exePath, target);
		else StringCchPrintfW(args, ARRAYSIZE(args), L"\"%s\"", exePath);

		STARTUPINFOW si = { sizeof(si) };
		PROCESS_INFORMATION pi = {};
		if (CreateProcessW(exePath, args, nullptr, nullptr, FALSE, CREATE_UNICODE_ENVIRONMENT | DETACHED_PROCESS, nullptr, nullptr, &si, &pi)) {
			CloseHandle(pi.hThread);
			CloseHandle(pi.hProcess);
		}
	}

	LONG m_ref;
};

class ClassFactory : public IClassFactory {
public:
	ClassFactory() : m_ref(1) { InterlockedIncrement(&g_refCount); }
	virtual ~ClassFactory() { InterlockedDecrement(&g_refCount); }

	IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override {
		if (!ppv) return E_POINTER;

		if (riid == IID_IUnknown || riid == IID_IClassFactory) {
			*ppv = static_cast<IClassFactory*>(this);
			AddRef();
			return S_OK;
		}

		*ppv = nullptr;
		return E_NOINTERFACE;
	}

	IFACEMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&m_ref); }

	IFACEMETHODIMP_(ULONG) Release() override {
		LONG ref = InterlockedDecrement(&m_ref);
		if (ref == 0) delete this;
		return ref;
	}

	IFACEMETHODIMP CreateInstance(IUnknown* outer, REFIID riid, void** ppv) override {
		if (outer) return CLASS_E_NOAGGREGATION;

		ExplorerCommand* command = new (std::nothrow) ExplorerCommand();
		if (!command) return E_OUTOFMEMORY;

		HRESULT hr = command->QueryInterface(riid, ppv);
		command->Release();
		return hr;
	}

	IFACEMETHODIMP LockServer(BOOL lock) override {
		if (lock) InterlockedIncrement(&g_refCount);
		else InterlockedDecrement(&g_refCount);
		return S_OK;
	}

private:
	LONG m_ref;
};

STDAPI DllGetClassObject(REFCLSID rclsid, REFIID riid, void** ppv) {
	if (rclsid != g_clsid) return CLASS_E_CLASSNOTAVAILABLE;

	ClassFactory* factory = new (std::nothrow) ClassFactory();
	if (!factory) return E_OUTOFMEMORY;

	HRESULT hr = factory->QueryInterface(riid, ppv);
	factory->Release();
	return hr;
}

STDAPI DllCanUnloadNow() { return g_refCount == 0 ? S_OK : S_FALSE; }

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID) {
	if (reason != DLL_PROCESS_ATTACH) return TRUE;

	g_module = instance;
	DisableThreadLibraryCalls(instance);
	return SUCCEEDED(CLSIDFromString(EXPLORER_COMMAND_CLSID, &g_clsid));
}
