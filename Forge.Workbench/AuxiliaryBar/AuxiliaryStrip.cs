using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Theming;
using Forge.UI.Controls;
using Forge.VSCode;

namespace Forge.Workbench;

public sealed class AuxiliaryStrip : Panel
{
	private const float ActionHeight = 34f;
	private const float ActionGap = 4f;
	private const float IndicatorRadius = 9f;
	private const float IconSize = 18f;

	private readonly AuxiliaryBar _bar;
	private readonly Dictionary<string, IVectorIcon?> _icons = new(StringComparer.Ordinal);
	private readonly List<(ActionButton Button, string ViewId)> _buttons = [];

	public AuxiliaryStrip(AuxiliaryBar bar) {
		_bar = bar;
		Name = "AuxiliaryStrip";
		MouseInputEnabled = false;
	}

	public Func<string, IVectorIcon?>? IconLoader { get; set; }

	public static float Width(float scale) => (Metrics.ActivityBarWidth + Metrics.CardMargin) * scale;

	public void Rebuild() {
		foreach ((ActionButton button, _) in _buttons) RemoveChild(button);

		_buttons.Clear();

		foreach (WebviewViewEntry view in _bar.Views) {
			string viewId = view.ViewId;
			ActionButton button = new() {
				Name = viewId,
				Icon = Codicon.Extensions,
				VectorIcon = IconFor(view),
				IconSize = IconSize,
				Dock = DockType.Top,
				Foreground = Theme.ActivityBarForeground,
				HoverForeground = Theme.Foreground,
				HoverBackground = Color.Transparent,
				CheckedRadius = IndicatorRadius,
				Action = () => _bar.Activate(viewId),
			};

			_buttons.Add((button, viewId));
			AddChild(button);
		}

		Sync();
		InvalidateLayout();
	}

	public void Sync() {
		foreach ((ActionButton button, string viewId) in _buttons) button.Checked = _bar.IsOpen && string.Equals(viewId, _bar.ActiveViewId, StringComparison.Ordinal);
	}

	private IVectorIcon? IconFor(WebviewViewEntry view) {
		if (view.IconPath is not { } path || IconLoader is not { } loader) return null;

		if (!_icons.TryGetValue(path, out IVectorIcon? icon)) _icons[path] = icon = loader(path);

		return icon;
	}

	public override void PerformLayout() {
		float scale = Scale;

		DockPadding = new Thickness(0f, 0f, Metrics.CardMargin * scale, 0f);

		foreach ((ActionButton button, _) in _buttons) {
			button.SetSize(Metrics.ActivityBarWidth * scale, ActionHeight * scale);
			button.DockMargin = new Thickness(0f, 0f, 0f, ActionGap * scale);
		}

		base.PerformLayout();
	}
}
