using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Core.Native;
using Forge.Theming;

namespace Forge.UI.Controls;

public sealed class QuickInput : Panel
{
	private const float PreferredWidth = 600f;
	private const float InputHeight = 26f;
	private const float RowHeight = 22f;
	private const float Padding = 6f;
	private const float TopOffset = 8f;
	private const int MaxRows = 12;

	private readonly TextEntry _entry = new() { Dock = DockType.Top };
	private readonly ListPanel _list = new() { Dock = DockType.Fill, RowHeight = RowHeight };

	private IQuickPickProvider? _provider;

	public QuickInput() {
		ContextKey = ContextKeys.InQuickOpen;
		DockPadding = Thickness.Uniform(Padding);

		AddChild(_entry);
		AddChild(_list);

		_entry.TextChanged = Filter;
		_list.Activated = Accept;
		_list.RowPainter = (painter, index, left, top, width, height, selected) => _provider?.PaintItem(painter, index, left, top, width, height, selected);
	}

	public bool IsOpen => IsPopup;

	public IQuickPickProvider? Provider => _provider;

	public void Show(IQuickPickProvider provider) {
		if (IsPopup && _provider is { } previous && previous != provider) previous.Cancel();

		_provider = provider;
		_entry.Placeholder = provider.GetPlaceholder();
		_entry.Clear();
		Filter();
		MakePopup(modal: true);
		_entry.RequestFocus();
	}

	public void Refilter() {
		if (IsPopup) Filter();
	}

	private void Filter() {
		if (_provider is null) return;

		_provider.Filter(_entry.Text);
		_list.SetCount(_provider.GetCount());
		_list.Select(0);
		InvalidateLayout();
	}

	private void Accept(int index) {
		IQuickPickProvider? provider = _provider;
		ClosePopup();
		provider?.Accept(index);
	}

	public override void OnPopupDismissed() {
		base.OnPopupDismissed();
		_provider?.Cancel();
	}

	public override bool OnKeyCodePressed(uint key, KeyModifiers modifiers) => key is VirtualKey.Up or VirtualKey.Down or VirtualKey.PageUp or VirtualKey.PageDown or VirtualKey.Return && _list.Navigate(key);

	public override void PerformLayout() {
		float scale = Scale;
		int rows = Math.Max(1, Math.Min(_list.Count, MaxRows));

		_entry.SetSize(_entry.Wide, InputHeight * scale);
		DockPadding = Thickness.Uniform(Padding * scale);
		_list.DockMargin = new Thickness(0f, Padding * scale, 0f, 0f);

		if (Ui is { } ui) {
			float wide = MathF.Min(PreferredWidth * scale, ui.GetViewportWidth() - (Metrics.Space16 * 2f * scale));
			float tall = (Padding * 3f * scale) + (InputHeight * scale) + (rows * RowHeight * scale);

			SetBounds((ui.GetViewportWidth() - wide) / 2f, (Metrics.TitleBarHeight + TopOffset) * scale, wide, tall);
		}

		base.PerformLayout();
	}

	public override void PaintBackground(IPainter painter) {
		float scale = Scale;
		float radius = Metrics.RadiusLarge * scale;

		painter.FillRoundedRectangle(ScreenX, ScreenY, Wide, Tall, radius, Theme.WidgetBackground);
		painter.StrokeRoundedRectangle(ScreenX, ScreenY, Wide, Tall, radius, Metrics.StrokeThickness * scale, Theme.SuggestBorder);
	}

	public override void PostChildPaint(IPainter painter) {
		if (_list.Count > 0 || _provider is null) return;

		ITextFont font = painter.GetUi();
		float top = _list.ScreenY;
		float baseline = top + (((RowHeight * Scale) - font.GetLineHeight()) / 2f) + font.GetBaseline();

		painter.DrawText(font, _provider.GetEmptyMessage(), _list.ScreenX + (Metrics.Space8 * Scale), baseline, Theme.DescriptionForeground);
	}
}
