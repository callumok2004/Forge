using Forge.Theming;

namespace Forge.Common.Editor;

public readonly record struct LineHint(string Text, bool Warning, Color? Color = null);
