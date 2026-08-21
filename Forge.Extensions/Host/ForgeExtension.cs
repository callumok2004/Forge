using Forge.Common.Commands;
using Forge.Common.Extensions;
using Forge.Core;

namespace Forge.Extensions.Host;

public abstract class ForgeExtension : IForgeExtension
{
	public abstract string Identifier { get; }

	public abstract string DisplayName { get; }

	public abstract ActivationEvent Activation { get; }

	public string GetIdentifier() => Identifier;

	public string GetDisplayName() => DisplayName;

	public ActivationEvent GetActivation() => Activation;

	public virtual bool ActivatesIn(string? workspacePath) => true;

	public virtual bool ActivatesFor(string filePath) => false;

	public virtual bool ActivatesForCommand(string commandId) =>
		commandId.StartsWith(CommandPrefix, StringComparison.OrdinalIgnoreCase);

	protected virtual string CommandPrefix =>
		Identifier.StartsWith("forge.", StringComparison.Ordinal) ? Identifier["forge.".Length..] + "." : Identifier + ".";

	public virtual void Register(ICommandRegistry commands) {
	}

	public void Activate(IExtensionContext context) {
		Configure(context.GetSettings());
		OnActivate(context);
	}

	public virtual void Deactivate() {
	}

	protected virtual void Configure(Settings settings) {
	}

	protected virtual void OnActivate(IExtensionContext context) {
	}
}
