using Forge.Common.Commands;
using Forge.Common.Extensions;
using Forge.Core;

namespace Forge.Extensions.Host;

public sealed class ExtensionHost(Settings settings, IReadOnlyList<IForgeExtension> extensions) : IExtensionHost
{
	private readonly Dictionary<string, ExtensionContext> _active = [with(StringComparer.Ordinal)];

	public IReadOnlyList<IForgeExtension> All() => extensions;

	public IReadOnlyList<string> Active() => [.. _active.Keys];

	public bool IsActive(string identifier) => _active.ContainsKey(identifier);

	public int RegisterAll(ICommandRegistry commands) {
		int before = commands.All().Count;

		foreach (IForgeExtension extension in extensions) extension.Register(commands);

		ExtensionCommands.Register(commands, this);

		return commands.All().Count - before;
	}

	public void Startup(ICommandHost host) {
		string? workspace = host.GetWorkspacePath();

		foreach (IForgeExtension extension in extensions) {
			ActivationEvent activation = extension.GetActivation();

			if (activation.HasFlag(ActivationEvent.Startup)) {
				Load(extension, host);
				continue;
			}

			if (activation.HasFlag(ActivationEvent.WorkspaceContains) && extension.ActivatesIn(workspace)) Load(extension, host);
		}
	}

	public void FileOpened(ICommandHost host, string path) {
		foreach (IForgeExtension extension in extensions) {
			if (IsActive(extension.GetIdentifier())) continue;

			if (extension.GetActivation().HasFlag(ActivationEvent.FileOpened) && extension.ActivatesFor(path)) Load(extension, host);
		}
	}

	public void CommandInvoked(ICommandHost host, string commandId) {
		foreach (IForgeExtension extension in extensions) {
			if (IsActive(extension.GetIdentifier())) continue;

			if (!extension.GetActivation().HasFlag(ActivationEvent.CommandInvoked)) continue;

			if (extension.ActivatesForCommand(commandId)) Load(extension, host);
		}
	}

	public bool Load(string identifier, ICommandHost host) {
		foreach (IForgeExtension extension in extensions) {
			if (string.Equals(extension.GetIdentifier(), identifier, StringComparison.OrdinalIgnoreCase)) return Load(extension, host);
		}

		return false;
	}

	public bool Unload(string identifier) {
		if (!_active.Remove(identifier, out ExtensionContext? context)) return false;

		foreach (IForgeExtension extension in extensions) {
			if (!string.Equals(extension.GetIdentifier(), identifier, StringComparison.OrdinalIgnoreCase)) continue;

			Guard(extension.Deactivate);
			break;
		}

		Guard(context.Dispose);
		return true;
	}

	public void UnloadAll() {
		foreach (string identifier in Active()) Unload(identifier);
	}

	private bool Load(IForgeExtension extension, ICommandHost host) {
		string identifier = extension.GetIdentifier();

		if (_active.ContainsKey(identifier)) return false;

		ExtensionContext context = new(identifier, host, settings);
		_active[identifier] = context;

		try {
			extension.Activate(context);
			return true;
		}
		catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException) {
			host.Log($"{extension.GetDisplayName()} failed to activate: {exception.Message}");
			_active.Remove(identifier);
			return false;
		}
	}

	private static void Guard(Action action) {
		try {
			action();
		}
		catch (Exception exception) when (exception is IOException or InvalidOperationException) {
		}
	}
}
