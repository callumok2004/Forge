namespace Forge.Common.Extensions;

[Flags]
public enum ActivationEvent
{
	Never = 0,

	Startup = 1 << 0,
	WorkspaceContains = 1 << 1,
	FileOpened = 1 << 2,
	CommandInvoked = 1 << 3,
}
