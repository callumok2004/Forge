using Forge.Common.Render;
using Forge.Core.Native;
using Forge.Theming;

namespace Forge.UI;

public enum TitleBarPart
{
	None,
	AppIcon,
	Menu,
	Account,
	CommandCentre,
	Branch,
	Layout,
	Settings,
	Minimize,
	Maximize,
	Close,
}

public sealed class TitleBar
{
	private const float ButtonWidth = 46f;
	private const float ActionWidth = 32f;
	private const float CommandCentreHeight = 22f;
	private const float CommandCentreMaxWidth = 600f;

	private const float CommandCentreWidthFraction = 0.38f;
	private const float CommandCentreMinWidth = 160f;

	private const string SignInLabel = "Sign in";

	private static readonly string[] MenuLabels = ["File", "Edit", "Selection", "View", "Go", "Run", "Help"];

	private TitleBarPart _hover = TitleBarPart.None;
	private int _hoverMenuIndex = -1;

	private float _menuLeft;
	private float _menuRight;
	private float _centreLeft;
	private float _centreRight;
	private float _branchLeft;
	private float _branchRight;
	private float _accountLeft;
	private float _accountRight;

	public static float Height(float scale) => Metrics.TitleBarHeight * scale;

	public string Title { get; set; } = "Forge";

	public string? DocumentName { get; set; }

	public string? FolderName { get; set; }

	public string? BranchName { get; set; }

	public string? RemoteOwner { get; set; }

	public string? RemoteRepository { get; set; }

	public string? AccountName { get; set; }

	public bool IsMaximized { get; set; }

	public IImage? Icon { get; set; }

	public TitleBarPart Hover => _hover;

	public int HoverMenuIndex => _hoverMenuIndex;

	public int OpenMenuIndex { get; set; } = -1;

	public static ReadOnlySpan<string> Menus => MenuLabels;

	public void SetHover(TitleBarPart part, int menuIndex = -1) {
		_hover = part;
		_hoverMenuIndex = part == TitleBarPart.Menu ? menuIndex : -1;
	}

	#region Hit testing

	public TitleBarPart PartAt(float x, float y, float windowWidth, float scale) {
		if (y < 0f || y >= Height(scale)) return TitleBarPart.None;

		float button = ButtonWidth * scale;
		float fromRight = windowWidth - x;

		if (fromRight >= 0f) {
			if (fromRight <= button) return TitleBarPart.Close;

			if (fromRight <= button * 2f) return TitleBarPart.Maximize;

			if (fromRight <= button * 3f) return TitleBarPart.Minimize;

			float action = ActionWidth * scale;
			float actionsRight = fromRight - (button * 3f);

			if (actionsRight <= action) return TitleBarPart.Settings;

			if (actionsRight <= action * 2f) return TitleBarPart.Layout;
		}

		if (x < Metrics.ActivityBarWidth * scale) return TitleBarPart.AppIcon;

		if (x >= _menuLeft && x < _menuRight) return TitleBarPart.Menu;

		if (_accountRight > _accountLeft && x >= _accountLeft && x < _accountRight) return TitleBarPart.Account;

		if (_branchRight > _branchLeft && x >= _branchLeft && x < _branchRight) return TitleBarPart.Branch;

		if (x >= _centreLeft && x < _centreRight) return TitleBarPart.CommandCentre;

		return TitleBarPart.None;
	}

	public int MenuIndexAt(IPainter painter, float x, float y, float scale) {
		if (y < 0f || y >= Height(scale) || x < _menuLeft || x >= _menuRight) return -1;

		float cursor = _menuLeft;

		for (int i = 0; i < MenuLabels.Length; i++) {
			float width = MenuItemWidth(painter, MenuLabels[i], scale);

			if (x >= cursor && x < cursor + width) return i;

			cursor += width;
		}

		return -1;
	}

	public static float MenuLeftAt(IPainter painter, int index, float scale) {
		float cursor = Metrics.ActivityBarWidth * scale;

		for (int i = 0; i < index && i < MenuLabels.Length; i++) {
			cursor += MenuItemWidth(painter, MenuLabels[i], scale);
		}

		return cursor;
	}

	public nint HitTest(float x, float y, float windowWidth, float scale) =>
		PartAt(x, y, windowWidth, scale) switch {
			TitleBarPart.Minimize => Win32.HtMinButton,
			TitleBarPart.Maximize => Win32.HtMaxButton,
			TitleBarPart.Close => Win32.HtClose,
			TitleBarPart.None => y < Height(scale) ? Win32.HtCaption : Win32.HtClient,
			_ => Win32.HtClient,
		};

	#endregion

	public void Render(IPainter painter, float width, float scale) {
		float height = Height(scale);

		painter.FillRectangle(0f, 0f, width, height, Theme.EditorBackground);

		float centreY = height / 2f;

		DrawAppIcon(painter, centreY, scale);
		DrawMenu(painter, centreY, scale);
		DrawCommandCentre(painter, width, centreY, scale);
		DrawActions(painter, width, centreY, height, scale);
		DrawCaptionButtons(painter, width, height, scale);
	}

	private void DrawAppIcon(IPainter painter, float centreY, float scale) {
		float slot = Metrics.ActivityBarWidth * scale;

		if (Icon is { } icon) painter.DrawImage(icon, slot / 2f, centreY, Metrics.IconSize * scale);
	}

	private void DrawMenu(IPainter painter, float centreY, float scale) {
		ITextFont font = painter.GetUi();
		float baseline = centreY - (font.GetLineHeight() / 2f) + font.GetBaseline();
		float itemHeight = 22f * scale;
		float radius = Metrics.RadiusSmall * scale;

		_menuLeft = Metrics.ActivityBarWidth * scale;
		float cursor = _menuLeft;

		for (int i = 0; i < MenuLabels.Length; i++) {
			float itemWidth = MenuItemWidth(painter, MenuLabels[i], scale);

			if (OpenMenuIndex == i || (_hover == TitleBarPart.Menu && _hoverMenuIndex == i)) {
				painter.FillRoundedRectangle(cursor, centreY - (itemHeight / 2f), itemWidth, itemHeight,
					radius, Theme.ListHoverBackground);
			}

			float textX = cursor + ((itemWidth - font.Measure(MenuLabels[i])) / 2f);
			painter.DrawText(font, MenuLabels[i], textX, baseline, Theme.TitleBarForeground);

			cursor += itemWidth;
		}

		_menuRight = cursor;
	}

	private static float MenuItemWidth(IPainter painter, string label, float scale) =>
		painter.GetUi().Measure(label) + (Metrics.Space8 * 2f * scale);

	private void DrawCommandCentre(IPainter painter, float windowWidth, float centreY, float scale) {
		ITextFont font = painter.GetUi();

		float gap = Metrics.Space6 * scale;
		float padding = Metrics.Space12 * scale;
		float iconSize = 14f * scale;
		float pillHeight = CommandCentreHeight * scale;

		ReadOnlySpan<char> label = DocumentName is { Length: > 0 } name ? name : Title;
		float labelWidth = font.Measure(label);

		ReadOnlySpan<char> suffix = DocumentName is { Length: > 0 } && FolderName is { Length: > 0 } folder
			? folder
			: default;

		float suffixWidth = suffix.IsEmpty ? 0f : font.Measure(suffix) + gap;

		float contentWidth = labelWidth + suffixWidth;
		float pillWidth = Math.Clamp(contentWidth + (padding * 2f), CommandCentreMinWidth * scale,
			CommandCentreMaxWidth * scale);

		bool hasRemote = RemoteRepository is { Length: > 0 };
		string? gitLabel = hasRemote ? RemoteRepository : BranchName;
		bool hasGit = gitLabel is { Length: > 0 };

		float gitWidth = hasGit
			? padding + iconSize + gap + font.Measure(gitLabel!) + padding
			: 0f;

		bool signedIn = AccountName is { Length: > 0 };
		string accountLabel = signedIn ? AccountName! : SignInLabel;
		float accountWidth = padding + iconSize + gap + font.Measure(accountLabel) + padding;

		float groupWidth = accountWidth + pillWidth + gitWidth;
		float groupLeft = (windowWidth - groupWidth) / 2f;

		float floor = _menuRight + (Metrics.Space8 * scale);
		float ceiling = windowWidth - ((ButtonWidth * 3f + ActionWidth * 2f) * scale) - groupWidth -
			(Metrics.Space8 * scale);

		if (ceiling < floor) {
			_centreLeft = _centreRight = _branchLeft = _branchRight = _accountLeft = _accountRight = 0f;
			return;
		}

		groupLeft = Math.Clamp(groupLeft, floor, ceiling);

		float top = centreY - (pillHeight / 2f);
		float radius = Metrics.RadiusMedium * scale;
		float baseline = centreY - (font.GetLineHeight() / 2f) + font.GetBaseline();

		_accountLeft = groupLeft;
		_accountRight = groupLeft + accountWidth;
		_centreLeft = _accountRight;
		_centreRight = _centreLeft + pillWidth;
		_branchLeft = hasGit ? _centreRight : 0f;
		_branchRight = hasGit ? _centreRight + gitWidth : 0f;

		float groupRight = groupLeft + groupWidth;

		painter.FillRoundedRectangle(groupLeft, top, groupWidth, pillHeight, radius,
			Theme.CommandCentreBackground);

		switch (_hover) {
			case TitleBarPart.Account:
				painter.FillRoundedRectangle(_accountLeft, top, accountWidth, pillHeight, radius,
					Theme.CommandCentreHoverBackground);

				painter.FillRectangle(_accountRight - radius, top, radius, pillHeight,
					Theme.CommandCentreHoverBackground);
				break;

			case TitleBarPart.CommandCentre:
				painter.FillRectangle(_centreLeft, top, pillWidth, pillHeight,
					Theme.CommandCentreHoverBackground);
				break;

			case TitleBarPart.Branch when hasGit:
				painter.FillRoundedRectangle(_branchLeft, top, gitWidth, pillHeight, radius,
					Theme.CommandCentreHoverBackground);

				painter.FillRectangle(_branchLeft, top, radius, pillHeight,
					Theme.CommandCentreHoverBackground);
				break;
		}

		painter.StrokeRoundedRectangle(groupLeft, top, groupWidth, pillHeight, radius, 1f,
			Theme.CommandCentreBorder);

		painter.DrawVerticalLine(_centreLeft, top, top + pillHeight, Theme.CommandCentreBorder);

		if (hasGit) painter.DrawVerticalLine(_branchLeft, top, top + pillHeight, Theme.CommandCentreBorder);

		painter.PushClip(_accountLeft, top, accountWidth, pillHeight);

		painter.DrawIcon(Codicon.Account, _accountLeft + padding + (iconSize / 2f), centreY, iconSize,
			Theme.TitleBarForeground);

		painter.DrawText(font, accountLabel, _accountLeft + padding + iconSize + gap, baseline,
			signedIn ? Theme.TitleBarForeground : Theme.SectionHeaderForeground);

		painter.PopClip();

		painter.PushClip(_centreLeft, top, pillWidth, pillHeight);

		float cursor = _centreLeft + Math.Max(padding, (pillWidth - contentWidth) / 2f);
		painter.DrawText(font, label, cursor, baseline, Theme.TitleBarForeground);

		if (!suffix.IsEmpty) {
			cursor += labelWidth + gap;
			painter.DrawText(font, suffix, cursor, baseline, Theme.SectionHeaderForeground);
		}

		painter.PopClip();

		if (!hasGit) return;

		painter.PushClip(_branchLeft, top, gitWidth, pillHeight);

		painter.DrawIcon(hasRemote ? Codicon.Repo : Codicon.GitBranch,
			_branchLeft + padding + (iconSize / 2f), centreY, iconSize, Theme.TitleBarForeground);

		painter.DrawText(font, gitLabel!, _branchLeft + padding + iconSize + gap, baseline,
			Theme.TitleBarForeground);

		painter.PopClip();
	}

	private void DrawActions(IPainter painter, float windowWidth, float centreY, float height, float scale) {
		float action = ActionWidth * scale;
		float buttons = ButtonWidth * 3f * scale;

		float settingsCentre = windowWidth - buttons - (action / 2f);
		float layoutCentre = settingsCentre - action;

		DrawAction(painter, Codicon.LayoutSidebarLeft, layoutCentre, centreY, height, action,
			TitleBarPart.Layout, scale);

		DrawAction(painter, Codicon.SettingsGear, settingsCentre, centreY, height, action,
			TitleBarPart.Settings, scale);
	}

	private void DrawAction(IPainter painter, Codicon icon, float centreX, float centreY, float height,
		float width, TitleBarPart part, float scale) {
		bool hovered = _hover == part;

		if (hovered) {
			float box = 24f * scale;
			painter.FillRoundedRectangle(centreX - (box / 2f), centreY - (box / 2f), box, box,
				Metrics.RadiusSmall * scale, Theme.ListHoverBackground);
		}

		painter.DrawIcon(icon, centreX, centreY, Metrics.IconSize * scale,
			hovered ? Theme.TabActiveForeground : Theme.TitleBarForeground);
	}

	private void DrawCaptionButtons(IPainter painter, float width, float height, float scale) {
		float button = ButtonWidth * scale;
		float centreY = height / 2f;

		DrawCaptionButton(painter, TitleBarPart.Minimize, width - (button * 3f), button, height, centreY, scale);
		DrawCaptionButton(painter, TitleBarPart.Maximize, width - (button * 2f), button, height, centreY, scale);
		DrawCaptionButton(painter, TitleBarPart.Close, width - button, button, height, centreY, scale);
	}

	private void DrawCaptionButton(IPainter painter, TitleBarPart which, float left, float width, float height,
		float centreY, float scale) {
		bool hovered = _hover == which;

		if (hovered) {
			painter.FillRectangle(left, 0f, width, height,
				which == TitleBarPart.Close ? Color.Parse("#C42B1C") : Theme.ListHoverBackground);
		}

		Color color = hovered && which == TitleBarPart.Close
			? Color.Parse("#FFFFFF")
			: Theme.TitleBarForeground;

		float centreX = left + (width / 2f);
		float size = 10f * scale;
		float half = size / 2f;

		switch (which) {
			case TitleBarPart.Minimize:
				painter.DrawHorizontalLine(centreY, centreX - half, centreX + half, color);
				break;

			case TitleBarPart.Maximize when IsMaximized:
				StrokeRectangle(painter, centreX - half, centreY - half + (2f * scale), size - (2f * scale),
					size - (2f * scale), color);
				painter.DrawHorizontalLine(centreY - half, centreX - half + (2f * scale), centreX + half, color);
				painter.DrawVerticalLine(centreX + half, centreY - half, centreY + half - (2f * scale), color);
				break;

			case TitleBarPart.Maximize:
				StrokeRectangle(painter, centreX - half, centreY - half, size, size, color);
				break;

			case TitleBarPart.Close:
				painter.StrokeLine(centreX - half, centreY - half, centreX + half, centreY + half, 1.2f, color);
				painter.StrokeLine(centreX + half, centreY - half, centreX - half, centreY + half, 1.2f, color);
				break;
		}
	}

	private static void StrokeRectangle(IPainter painter, float x, float y, float width, float height,
		Color color) {
		painter.DrawHorizontalLine(y, x, x + width, color);
		painter.DrawHorizontalLine(y + height, x, x + width, color);
		painter.DrawVerticalLine(x, y, y + height, color);
		painter.DrawVerticalLine(x + width, y, y + height + 1f, color);
	}
}
