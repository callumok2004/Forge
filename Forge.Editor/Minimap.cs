using System.Diagnostics;

using Forge.Common.Commands;
using Forge.Common.Git;
using Forge.Common.Render;
using Forge.Text;
using Forge.Theming;

namespace Forge.Editor;

public sealed class MinimapSettings
{
	public bool Enabled { get; init; } = true;

	public string Autohide { get; init; } = "none";

	public string Size { get; init; } = "proportional";

	public string Side { get; init; } = "right";

	public string ShowSlider { get; init; } = "mouseover";

	public int Scale { get; init; } = 1;

	public bool RenderCharacters { get; init; } = true;

	public int MaxColumn { get; init; } = 120;

	public bool ShowRegionSectionHeaders { get; init; } = true;

	public bool ShowMarkSectionHeaders { get; init; } = true;

	public string MarkSectionHeaderRegex { get; init; } = @"\bMARK:\s*(?<separator>-?)\s*(?<label>.*)$";

	public float SectionHeaderFontSize { get; init; } = 9f;

	public float SectionHeaderLetterSpacing { get; init; } = 1f;
}

public sealed class Minimap
{
	public const float GutterWidth = 8f;
	public const float VerticalScrollbarWidth = 14f;
	private const float PointerDragResetDistance = 140f;
	private const int GutterDecorationWidth = 2;
	private const string DefaultFontFamily = "Segoe WPC";

	private enum RenderKind { None, Text, Blocks }

	private readonly record struct Options(
		RenderKind Render,
		string Size,
		bool HeightIsEditorHeight,
		bool ScrollBeyondLastLine,
		string ShowSlider,
		string Autohide,
		float PixelRatio,
		float LineHeight,
		float MinimapLeft,
		float MinimapWidth,
		float MinimapHeight,
		int CanvasInnerWidth,
		int CanvasInnerHeight,
		float CanvasOuterWidth,
		float CanvasOuterHeight,
		bool IsSampling,
		float EditorHeight,
		int FontScale,
		int MinimapLineHeight,
		int MinimapCharWidth,
		float SectionHeaderFontSize,
		float SectionHeaderLetterSpacing);

	private sealed class Layout(float scrollTop, float scrollHeight, bool sliderNeeded, float computedSliderRatio, float sliderTop, float sliderHeight, int topPaddingLineCount, int startLineNumber, int endLineNumber)
	{
		public float ScrollTop { get; } = scrollTop;
		public float ScrollHeight { get; } = scrollHeight;
		public bool SliderNeeded { get; } = sliderNeeded;
		public float SliderTop { get; } = sliderTop;
		public float SliderHeight { get; } = sliderHeight;
		public int TopPaddingLineCount { get; } = topPaddingLineCount;
		public int StartLineNumber { get; } = startLineNumber;
		public int EndLineNumber { get; } = endLineNumber;

		public float DesiredScrollTopFromDelta(float delta) => MathF.Round(ScrollTop + (delta / computedSliderRatio));

		public (int Start, int End)? Intersect(int startLineNumber, int endLineNumber) {
			int start = Math.Max(StartLineNumber, startLineNumber);
			int end = Math.Min(EndLineNumber, endLineNumber);
			return start > end ? null : (start, end);
		}

		public float YForLineNumber(int lineNumber, int minimapLineHeight) => (lineNumber - StartLineNumber + TopPaddingLineCount) * minimapLineHeight;
	}

	private sealed class RenderData(Layout layout, PixelBuffer buffer, int[] lines)
	{
		public Layout Layout { get; } = layout;
		public PixelBuffer Buffer { get; } = buffer;
		public int[] Lines { get; set; } = lines;
		public int StartLineNumber { get; set; } = layout.StartLineNumber;

		public bool LinesEqual(Layout other) {
			if (Layout.StartLineNumber != other.StartLineNumber || Layout.EndLineNumber != other.EndLineNumber) return false;

			foreach (int dy in Lines) {
				if (dy == -1) return false;
			}

			return true;
		}

		public void Changed(int from, int count) {
			for (int i = 0; i < count; i++) {
				int index = from + i - StartLineNumber;

				if ((uint)index < (uint)Lines.Length) Lines[index] = -1;
			}
		}

		public void Deleted(int from, int to) {
			int first = from - StartLineNumber;
			int last = to - StartLineNumber;
			int count = to - from + 1;

			if (last < 0) {
				StartLineNumber -= count;
				return;
			}

			if (first >= Lines.Length) return;

			int removeStart = Math.Max(0, first);
			int removeEnd = Math.Min(Lines.Length - 1, last);
			int[] next = new int[Lines.Length - (removeEnd - removeStart + 1)];

			Array.Copy(Lines, 0, next, 0, removeStart);
			Array.Copy(Lines, removeEnd + 1, next, removeStart, Lines.Length - removeEnd - 1);

			if (first < 0) StartLineNumber = from;

			Lines = next;
		}

		public void Inserted(int from, int to) {
			int count = to - from + 1;
			int index = from - StartLineNumber;

			if (index <= 0) {
				StartLineNumber += count;
				return;
			}

			if (index >= Lines.Length) return;

			int[] next = new int[Lines.Length + count];

			Array.Copy(Lines, 0, next, 0, index);
			Array.Fill(next, -1, index, count);
			Array.Copy(Lines, index, next, index + count, Lines.Length - index);
			Lines = next;
		}
	}

	private sealed class SamplingState(float ratio, int[] lines)
	{
		public float Ratio { get; } = ratio;
		public int[] Lines { get; } = lines;

		public int ModelLineToMinimapLine(int lineNumber) => Math.Min(Lines.Length, Math.Max(1, (int)MathF.Round(lineNumber / Ratio)));

		public (int Start, int End) DecorationRange(int startLineNumber, int endLineNumber) {
			int start = ModelLineToMinimapLine(startLineNumber);
			int end = ModelLineToMinimapLine(endLineNumber);

			if (startLineNumber != endLineNumber && end == start) {
				if (end == Lines.Length) {
					if (start > 1) start--;
				}
				else end++;
			}

			return (start, end);
		}
	}

	private readonly record struct Decoration(int StartLine, int StartColumn, int EndLine, int EndColumn, Color Color, bool Gutter, int ZIndex);

	private MinimapSettings _settings = new();
	private Options _options;
	private bool _hasOptions;
	private RenderData? _lastRenderData;
	private readonly PixelBuffer[] _buffers = [new PixelBuffer(), new PixelBuffer()];
	private int _lastBuffer;
	private SamplingState? _sampling;
	private MinimapCharRenderer? _charRenderer;
	private TextView? _view;
	private TextSnapshot? _snapshot;
	private object? _semantics;
	private int[] _viewToModel = [];
	private int _viewLineCount;
	private uint[] _colors = new uint[256];
	private char[] _chars = new char[256];

	private List<MinimapSectionHeader> _sectionHeaders = [];
	private int _sectionHeadersVersion = -1;
	private TextView? _sectionHeadersView;
	private readonly Stopwatch _sectionHeadersDelay = new();
	private readonly Dictionary<string, string> _sectionHeaderCache = [];

	private float _x;
	private float _y;
	private bool _mouseOver;
	private bool _sliderHover;
	private bool _dragging;
	private float _dragStartY;
	private float _dragStartX;
	private Layout? _dragLayout;
	private float _sliderOpacity;
	private float _opacity = 1f;
	private float _lastScrollTop = float.NaN;
	private float _lastScrollHeight = float.NaN;
	private readonly Stopwatch _hideDelay = new();
	private bool _active;
	private Layout? _layout;

	public MinimapSettings Settings {
		get => _settings;
		set {
			_settings = value;
			_sliderOpacity = SliderTargetOpacity;
			_hasOptions = false;
			_lastRenderData = null;
			_sectionHeadersVersion = -1;
			_sectionHeaderCache.Clear();
		}
	}

	public IReadOnlyList<DocumentHighlight> Occurrences { get; set; } = [];

	public Action? RequestTick { get; set; }

	public bool Enabled => _settings.Enabled;

	public bool IsLeft => _settings.Side == "left";

	public float Width { get; private set; }

	public bool WantsTick =>
		_sliderOpacity != SliderTargetOpacity ||
		_opacity != TargetOpacity ||
		(_settings.Autohide == "scroll" && _active) ||
		_sectionHeadersDelay.IsRunning;

	private float SliderTargetOpacity => _settings.ShowSlider == "always" || _mouseOver || _dragging ? 1f : 0f;

	private float TargetOpacity => _settings.Autohide switch {
		"mouseover" => _mouseOver ? 1f : 0f,
		"scroll" => _active ? 1f : 0f,
		_ => 1f,
	};

	public bool AcceptsPointer => _settings.Autohide != "scroll" || _opacity > 0f;

	public void Tick(float deltaSeconds) {
		_sliderOpacity = Approach(_sliderOpacity, SliderTargetOpacity, deltaSeconds / 0.1f);
		_opacity = Approach(_opacity, TargetOpacity, deltaSeconds / 0.5f);

		if (_active && _hideDelay.ElapsedMilliseconds >= 500) {
			if (_mouseOver) _hideDelay.Restart();
			else {
				_active = false;
				_hideDelay.Reset();
			}
		}
	}

	private static float Approach(float value, float target, float step) => value < target ? Math.Min(target, value + step) : Math.Max(target, value - step);

	private static int ViewLineCount(TextView view) => view.HasHiddenLines ? CountVisible(view) : view.Snapshot.LineCount;

	private static int CountVisible(TextView view) {
		int count = 0;

		for (int line = 0; line < view.Snapshot.LineCount; line++) {
			if (!view.IsHiddenLine(line)) count++;
		}

		return count;
	}

	private static (int TypicalViewportLineCount, int ExtraBefore, float ExtraBeyond, float DesiredRatio, int MinimapLineCount) ContainedLineCount(int viewLineCount, bool scrollBeyondLastLine, float height, float lineHeight, float pixelRatio) {
		float typicalViewportLineCount = height / lineHeight;
		int extraLinesBeforeFirstLine = 0;
		float extraLinesBeyondLastLine = 0;

		if (scrollBeyondLastLine) extraLinesBeyondLastLine = Math.Max(extraLinesBeyondLastLine, typicalViewportLineCount - 1);

		float desiredRatio = (extraLinesBeforeFirstLine + viewLineCount + extraLinesBeyondLastLine) / (pixelRatio * height);
		int minimapLineCount = (int)MathF.Floor(viewLineCount / desiredRatio);
		return ((int)typicalViewportLineCount, extraLinesBeforeFirstLine, extraLinesBeyondLastLine, desiredRatio, minimapLineCount);
	}

	public float ComputeWidth(TextView view, ITextFont font, float outerWidth, float outerHeight, float scale) {
		if (!_settings.Enabled) {
			Width = 0f;
			return 0f;
		}

		float pixelRatio = scale;
		float lineHeight = font.GetLineHeight() / scale;
		float typicalHalfwidthCharacterWidth = font.GetCellWidth() / scale;
		float cssWidth = outerWidth / scale;
		float cssHeight = outerHeight / scale;
		float remainingWidth = cssWidth - (view.GutterWidth(font) / scale);
		int viewLineCount = ViewLineCount(view);
		bool renderCharacters = _settings.RenderCharacters;
		int minimapScale = pixelRatio >= 2 ? (int)MathF.Round(_settings.Scale * 2) : _settings.Scale;
		int baseCharHeight = renderCharacters ? 2 : 3;
		int canvasInnerHeight = (int)MathF.Floor(pixelRatio * cssHeight);
		float canvasOuterHeight = canvasInnerHeight / pixelRatio;
		bool heightIsEditorHeight = false;
		bool isSampling = false;
		int minimapLineHeight = baseCharHeight * minimapScale;
		float minimapCharWidth = minimapScale / pixelRatio;
		float widthMultiplier = 1f;

		if (_settings.Size is "fill" or "fit") {
			(int typicalViewportLineCount, int extraBefore, float extraBeyond, float desiredRatio, int minimapLineCount) = ContainedLineCount(viewLineCount, view.ScrollBeyondLastLine, cssHeight, lineHeight, pixelRatio);
			float ratio = (float)viewLineCount / minimapLineCount;

			if (ratio > 1) {
				heightIsEditorHeight = true;
				isSampling = true;
				minimapScale = 1;
				minimapLineHeight = 1;
				minimapCharWidth = minimapScale / pixelRatio;
			}
			else {
				bool fitBecomesFill = false;
				int maxMinimapScale = minimapScale + 1;

				if (_settings.Size == "fit") {
					float effectiveMinimapHeight = MathF.Ceiling((extraBefore + viewLineCount + extraBeyond) * minimapLineHeight);
					fitBecomesFill = effectiveMinimapHeight > canvasInnerHeight;
				}

				if (_settings.Size == "fill" || fitBecomesFill) {
					heightIsEditorHeight = true;
					int configuredMinimapScale = minimapScale;
					minimapLineHeight = (int)Math.Min(lineHeight * pixelRatio, Math.Max(1, MathF.Floor(1 / desiredRatio)));
					minimapScale = Math.Min(maxMinimapScale, Math.Max(1, minimapLineHeight / baseCharHeight));

					if (minimapScale > configuredMinimapScale) widthMultiplier = Math.Min(2f, (float)minimapScale / configuredMinimapScale);

					minimapCharWidth = minimapScale / pixelRatio / widthMultiplier;
					canvasInnerHeight = (int)MathF.Ceiling(Math.Max(typicalViewportLineCount, extraBefore + viewLineCount + extraBeyond) * minimapLineHeight);
				}
			}
		}

		float minimapMaxWidth = MathF.Floor(_settings.MaxColumn * minimapCharWidth);
		float minimapWidth = Math.Min(minimapMaxWidth, Math.Max(0f, MathF.Floor((remainingWidth - VerticalScrollbarWidth - 2) * minimapCharWidth / (typicalHalfwidthCharacterWidth + minimapCharWidth))) + GutterWidth);
		int canvasInnerWidth = (int)MathF.Floor(pixelRatio * minimapWidth);
		float canvasOuterWidth = canvasInnerWidth / pixelRatio;
		canvasInnerWidth = (int)MathF.Floor(canvasInnerWidth * widthMultiplier);
		float minimapLeft = _settings.Side == "left" ? 0f : cssWidth - minimapWidth - VerticalScrollbarWidth;

		Options options = new(
			renderCharacters ? RenderKind.Text : RenderKind.Blocks,
			_settings.Size,
			heightIsEditorHeight,
			view.ScrollBeyondLastLine,
			_settings.ShowSlider,
			_settings.Autohide,
			pixelRatio,
			lineHeight,
			minimapLeft,
			minimapWidth,
			cssHeight,
			canvasInnerWidth,
			canvasInnerHeight,
			canvasOuterWidth,
			canvasOuterHeight,
			isSampling,
			cssHeight,
			minimapScale,
			minimapLineHeight,
			MinimapCharRenderer.BaseCharWidth * minimapScale,
			_settings.SectionHeaderFontSize * pixelRatio,
			_settings.SectionHeaderLetterSpacing);

		if (!_hasOptions || options != _options) {
			_options = options;
			_hasOptions = true;
			_lastRenderData = null;
			_charRenderer = null;
		}

		Width = MathF.Round(minimapWidth * scale);
		return Width;
	}

	private void SyncModel(TextView view) {
		TextSnapshot snapshot = view.Snapshot;

		if (!ReferenceEquals(view, _view)) {
			_view = view;
			_lastRenderData = null;
		}

		if (!ReferenceEquals(view.Semantics, _semantics)) {
			_semantics = view.Semantics;
			_lastRenderData = null;
		}

		int edited = view.TakeEditedLine();

		if (_snapshot is { } previous && !ReferenceEquals(previous, snapshot) && _lastRenderData is { } data) {
			int line = edited >= 0 ? edited : 0;
			int delta = snapshot.LineCount - previous.LineCount;

			if (_sampling is not null || view.HasHiddenLines) _lastRenderData = null;
			else {
				if (delta < 0) data.Deleted(line + 2, line + 1 - delta);
				else if (delta > 0) data.Inserted(line + 2, line + 1 + delta);

				data.Changed(line + 1, Math.Max(1, snapshot.LineCount - line));
			}
		}

		_snapshot = snapshot;

		if (view.HasHiddenLines) {
			if (_viewToModel.Length < snapshot.LineCount) _viewToModel = new int[snapshot.LineCount];

			int count = 0;

			for (int line = 0; line < snapshot.LineCount; line++) {
				if (!view.IsHiddenLine(line)) _viewToModel[count++] = line;
			}

			if (count != _viewLineCount) _lastRenderData = null;

			_viewLineCount = count;
		}
		else {
			if (_viewLineCount != snapshot.LineCount && _sampling is not null) _lastRenderData = null;

			_viewLineCount = snapshot.LineCount;
		}

		ComputeSampling();
	}

	private void ComputeSampling() {
		if (_options.Render == RenderKind.None || !_options.IsSampling) {
			if (_sampling is not null) _lastRenderData = null;

			_sampling = null;
			return;
		}

		int viewLineCount = _viewLineCount;
		(_, _, _, _, int minimapLineCount) = ContainedLineCount(viewLineCount, _options.ScrollBeyondLastLine, _options.EditorHeight, _options.LineHeight, _options.PixelRatio);
		float ratio = (float)viewLineCount / minimapLineCount;
		float halfRatio = ratio / 2;
		int[] result = new int[Math.Max(1, minimapLineCount)];
		result[0] = 1;

		if (minimapLineCount > 1) {
			for (int i = 0; i < minimapLineCount - 1; i++) result[i] = (int)MathF.Round((i * ratio) + halfRatio);

			result[minimapLineCount - 1] = viewLineCount;
		}

		if (_sampling is { } old && old.Ratio == ratio && old.Lines.AsSpan().SequenceEqual(result)) return;

		_sampling = new SamplingState(ratio, result);
		_lastRenderData = null;
	}

	private int ModelLine(int viewLineNumber) {
		int index = viewLineNumber - 1;

		if (_view is { HasHiddenLines: true }) return (uint)index < (uint)_viewLineCount ? _viewToModel[index] : Math.Max(0, _viewLineCount - 1);

		return index;
	}

	private int ViewLineOf(int modelLine) {
		if (_view is not { HasHiddenLines: true }) return modelLine + 1;

		int low = 0;
		int high = _viewLineCount - 1;

		while (low <= high) {
			int mid = (low + high) >> 1;

			if (_viewToModel[mid] < modelLine) low = mid + 1;
			else high = mid - 1;
		}

		return Math.Clamp(low, 0, Math.Max(0, _viewLineCount - 1)) + 1;
	}

	private int LineCount => _sampling?.Lines.Length ?? _viewLineCount;

	private int MinimapModelLine(int minimapLineNumber) => ModelLine(_sampling is { } sampling ? sampling.Lines[minimapLineNumber - 1] : minimapLineNumber);

	private int MinimapLineOfView(int viewLineNumber) => _sampling?.ModelLineToMinimapLine(viewLineNumber) ?? viewLineNumber;

	private Layout CreateLayout(int viewportStartLineNumber, int viewportEndLineNumber, float viewportStartLineNumberVerticalOffset, float viewportHeight, bool viewportContainsWhitespaceGaps, int lineCount, int realLineCount, float scrollTop, float scrollHeight, Layout? previousLayout) {
		Options options = _options;
		float pixelRatio = options.PixelRatio;
		int minimapLineHeight = options.MinimapLineHeight;
		int minimapLinesFitting = options.CanvasInnerHeight / minimapLineHeight;
		float lineHeight = options.LineHeight;

		if (options.HeightIsEditorHeight) {
			float logicalScrollHeight = realLineCount * lineHeight;

			if (options.ScrollBeyondLastLine) logicalScrollHeight += Math.Max(0, viewportHeight - lineHeight);

			float sliderHeight = Math.Max(1, MathF.Floor(viewportHeight * viewportHeight / logicalScrollHeight));
			float maxMinimapSliderTop = Math.Max(0, options.MinimapHeight - sliderHeight);
			float computedSliderRatio = maxMinimapSliderTop / (scrollHeight - viewportHeight);
			float sliderTop = scrollTop * computedSliderRatio;
			int maxLinesFitting = options.CanvasInnerHeight / options.MinimapLineHeight;
			return new Layout(scrollTop, scrollHeight, maxMinimapSliderTop > 0, computedSliderRatio, sliderTop, sliderHeight, 0, 1, Math.Min(lineCount, maxLinesFitting));
		}

		float height;

		if (viewportContainsWhitespaceGaps && viewportEndLineNumber != lineCount) height = MathF.Floor((viewportEndLineNumber - viewportStartLineNumber + 1) * minimapLineHeight / pixelRatio);
		else height = MathF.Floor(viewportHeight / lineHeight * minimapLineHeight / pixelRatio);

		int extraLinesAtTheTop = 0;
		float extraLinesAtTheBottom = 0;

		if (options.ScrollBeyondLastLine) extraLinesAtTheBottom = Math.Max(extraLinesAtTheBottom, (viewportHeight / lineHeight) - 1);

		float maxSliderTop;

		if (extraLinesAtTheBottom > 0) maxSliderTop = (extraLinesAtTheTop + lineCount + extraLinesAtTheBottom - (viewportHeight / lineHeight) - 1) * minimapLineHeight / pixelRatio;
		else maxSliderTop = Math.Max(0, ((extraLinesAtTheTop + lineCount) * minimapLineHeight / pixelRatio) - height);

		maxSliderTop = Math.Min(options.MinimapHeight - height, maxSliderTop);

		float ratio = maxSliderTop / (scrollHeight - viewportHeight);
		float top = scrollTop * ratio;

		if (minimapLinesFitting >= extraLinesAtTheTop + lineCount + extraLinesAtTheBottom) return new Layout(scrollTop, scrollHeight, maxSliderTop > 0, ratio, top, height, extraLinesAtTheTop, 1, lineCount);

		float consideringStartLineNumber = viewportStartLineNumber > 1 ? viewportStartLineNumber + extraLinesAtTheTop : Math.Max(1, scrollTop / lineHeight);
		int topPaddingLineCount;
		int startLineNumber = (int)Math.Max(1, MathF.Floor(consideringStartLineNumber - (top * pixelRatio / minimapLineHeight)));

		if (startLineNumber < extraLinesAtTheTop) {
			topPaddingLineCount = extraLinesAtTheTop - startLineNumber + 1;
			startLineNumber = 1;
		}
		else {
			topPaddingLineCount = 0;
			startLineNumber = Math.Max(1, startLineNumber - extraLinesAtTheTop);
		}

		if (previousLayout is not null && previousLayout.ScrollHeight == scrollHeight) {
			if (previousLayout.ScrollTop > scrollTop) {
				startLineNumber = Math.Min(startLineNumber, previousLayout.StartLineNumber);
				topPaddingLineCount = Math.Max(topPaddingLineCount, previousLayout.TopPaddingLineCount);
			}

			if (previousLayout.ScrollTop < scrollTop) {
				startLineNumber = Math.Max(startLineNumber, previousLayout.StartLineNumber);
				topPaddingLineCount = Math.Min(topPaddingLineCount, previousLayout.TopPaddingLineCount);
			}
		}

		int endLineNumber = Math.Min(lineCount, startLineNumber - topPaddingLineCount + minimapLinesFitting - 1);
		float partialLine = (scrollTop - viewportStartLineNumberVerticalOffset) / lineHeight;
		float sliderTopAligned = scrollTop >= 0 ? (viewportStartLineNumber - startLineNumber + topPaddingLineCount + partialLine) * minimapLineHeight / pixelRatio : 0;

		return new Layout(scrollTop, scrollHeight, true, ratio, sliderTopAligned, height, topPaddingLineCount, startLineNumber, endLineNumber);
	}

	public void Render(IPainter painter, TextView view, float x, float y, float height, float scale) {
		_x = x;
		_y = y;

		if (!_settings.Enabled || !_hasOptions || Width <= 0f) return;

		SyncModel(view);
		UpdateSectionHeaders(view);

		float lineHeight = view.LastLineHeight;
		float cssLineHeight = _options.LineHeight;
		float scrollTop = view.ScrollY / scale;
		float viewportHeight = height / scale;
		float scrollHeight = Math.Max(viewportHeight, view.ScrollHeight(lineHeight, height) / scale);
		int firstModel = view.LineAtY(view.ScrollY, lineHeight);
		int lastModel = view.LineAtY(view.ScrollY + height - 1f, lineHeight);
		int viewportStart = ViewLineOf(firstModel);
		int viewportEnd = ViewLineOf(lastModel);
		bool gaps = false;

		foreach (TextView.ViewZone zone in view.Zones) {
			if (zone.Height > 0f && zone.AfterLine >= firstModel - 1 && zone.AfterLine <= lastModel) gaps = true;
		}

		float viewportStartOffset = view.TopOf(ModelLine(viewportStart), lineHeight) / scale;

		viewportStart = MinimapLineOfView(viewportStart);
		viewportEnd = MinimapLineOfView(viewportEnd);

		if (_settings.Autohide == "scroll" && ((!float.IsNaN(_lastScrollTop) && scrollTop != _lastScrollTop) || (!float.IsNaN(_lastScrollHeight) && scrollHeight != _lastScrollHeight))) {
			_active = true;
			_hideDelay.Restart();
		}

		_lastScrollTop = scrollTop;
		_lastScrollHeight = scrollHeight;

		Layout layout = CreateLayout(viewportStart, viewportEnd, viewportStartOffset, viewportHeight, gaps, LineCount, _viewLineCount, scrollTop, scrollHeight, _lastRenderData?.Layout);
		_layout = layout;

		float width = Width;
		float opacity = _opacity;

		painter.FillRectangle(x, y, width, height, view.Background);

		if (opacity <= 0f) {
			if (WantsTick) RequestTick?.Invoke();

			return;
		}

		painter.PushClip(x, y, width, height);

		_lastRenderData = RenderLines(painter, view, layout);

		if (_lastRenderData is { } data) painter.DrawPixels(data.Buffer, x, y, _options.CanvasOuterWidth * scale, _options.CanvasOuterHeight * scale, 0.9f * opacity);

		RenderDecorations(painter, view, layout, x, y, scale, opacity);

		if (layout.SliderNeeded && _sliderOpacity > 0f) {
			Color slider = _dragging ? Theme.MinimapSliderActive : _sliderHover ? Theme.MinimapSliderHover : Theme.MinimapSlider;
			painter.FillRectangle(x, y + (layout.SliderTop * scale), width, layout.SliderHeight * scale, slider.WithAlpha(slider.A * _sliderOpacity * opacity));
		}

		painter.PopClip();

		if (view.ScrollX + view.TextAreaWidth < view.ContentWidth) DrawShadow(painter, x, y, height, scale, opacity);

		if (WantsTick) RequestTick?.Invoke();
	}

	private static void DrawShadow(IPainter painter, float x, float y, float height, float scale, float opacity) {
		float blur = 6f * scale;
		int steps = Math.Max(1, (int)MathF.Ceiling(blur));

		for (int i = 0; i < steps; i++) {
			float t = 1f - ((i + 0.5f) / steps);
			painter.FillRectangle(x - blur + (i * blur / steps), y, blur / steps, height, Theme.MinimapShadow.WithAlpha(0.08f * t * opacity));
		}
	}

	private PixelBuffer NextBuffer() {
		_lastBuffer = 1 - _lastBuffer;
		PixelBuffer buffer = _buffers[_lastBuffer];
		buffer.Resize(_options.CanvasInnerWidth, _options.CanvasInnerHeight);
		Rgba8 background = Rgba8.From(Theme.MinimapBackground);
		buffer.Span.Fill(MinimapCharRenderer.Pixel(background.R, background.G, background.B, background.A));
		return buffer;
	}

	private MinimapCharRenderer CharRenderer(IPainter painter) =>
		_charRenderer ??= MinimapCharRenderer.Create(_options.FontScale, painter.GetCode().GetFamilyName(), () => painter.RasterizeGlyphs(painter.GetCode().GetFamilyName(), MinimapCharRenderer.SampledCharHeight, MinimapCharRenderer.SampleCharacters(), MinimapCharRenderer.SampledCharWidth, MinimapCharRenderer.SampledCharHeight));

	private RenderData? RenderLines(IPainter painter, TextView view, Layout layout) {
		int startLineNumber = layout.StartLineNumber;
		int endLineNumber = layout.EndLineNumber;
		int minimapLineHeight = _options.MinimapLineHeight;

		if (_lastRenderData is { } last && last.LinesEqual(layout)) return new RenderData(layout, last.Buffer, last.Lines);

		if (_options.CanvasInnerWidth <= 0 || _options.CanvasInnerHeight <= 0) return null;

		PixelBuffer buffer = NextBuffer();
		int count = Math.Max(0, endLineNumber - startLineNumber + 1);
		bool[] needed = RenderUntouchedLines(buffer, layout.TopPaddingLineCount, startLineNumber, endLineNumber, minimapLineHeight, _lastRenderData);
		Rgba8 defaultBackground = Rgba8.From(view.Background);
		Rgba8 background = Rgba8.From(Theme.MinimapBackground);
		int foregroundAlpha = (int)MathF.Round(255f * Theme.MinimapForegroundOpacity);
		bool useLighterFont = RelativeLuminance(defaultBackground) >= 0.5;
		MinimapCharRenderer charRenderer = CharRenderer(painter);
		int fontScale = _options.FontScale;
		int charWidth = _options.MinimapCharWidth;
		int baseCharHeight = _options.Render == RenderKind.Text ? MinimapCharRenderer.BaseCharHeight : MinimapCharRenderer.BaseCharHeight + 1;
		int renderLineHeight = baseCharHeight * fontScale;
		int innerLinePadding = minimapLineHeight > renderLineHeight ? (minimapLineHeight - renderLineHeight) / 2 : 0;
		float backgroundA = background.A / 255f;
		Rgba8 renderBackground = new((byte)MathF.Round(((background.R - defaultBackground.R) * backgroundA) + defaultBackground.R), (byte)MathF.Round(((background.G - defaultBackground.G) * backgroundA) + defaultBackground.G), (byte)MathF.Round(((background.B - defaultBackground.B) * backgroundA) + defaultBackground.B), 255);
		int dy = layout.TopPaddingLineCount * minimapLineHeight;
		int[] rendered = new int[count];
		int tabSize = view.TabWidth;

		for (int i = 0; i < count; i++) {
			if (needed[i]) RenderLine(buffer, renderBackground, background.A, useLighterFont, charWidth, foregroundAlpha, charRenderer, dy, innerLinePadding, tabSize, view, MinimapModelLine(startLineNumber + i), fontScale, minimapLineHeight);

			rendered[i] = dy;
			dy += minimapLineHeight;
		}

		buffer.Commit();
		return new RenderData(layout, buffer, rendered);
	}

	private static double RelativeLuminance(Rgba8 color) {
		static double Component(byte value) {
			double c = value / 255.0;
			return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
		}

		return Math.Round((0.2126 * Component(color.R)) + (0.7152 * Component(color.G)) + (0.0722 * Component(color.B)), 4);
	}

	private bool[] RenderUntouchedLines(PixelBuffer target, int topPaddingLineCount, int startLineNumber, int endLineNumber, int minimapLineHeight, RenderData? last) {
		int count = Math.Max(0, endLineNumber - startLineNumber + 1);
		bool[] needed = new bool[count];

		if (last is null || last.Buffer.Width != target.Width || last.Buffer.Height != target.Height) {
			Array.Fill(needed, true);
			return needed;
		}

		uint[] source = last.Buffer.Pixels;
		uint[] dest = target.Pixels;
		int width = target.Width;
		int destDy = topPaddingLineCount * minimapLineHeight;

		for (int lineNumber = startLineNumber; lineNumber <= endLineNumber; lineNumber++) {
			int lineIndex = lineNumber - startLineNumber;
			int lastIndex = lineNumber - last.StartLineNumber;
			int sourceDy = lastIndex >= 0 && lastIndex < last.Lines.Length ? last.Lines[lastIndex] : -1;

			if (sourceDy == -1 || sourceDy + minimapLineHeight > target.Height || destDy + minimapLineHeight > target.Height) {
				needed[lineIndex] = true;
				destDy += minimapLineHeight;
				continue;
			}

			Array.Copy(source, sourceDy * width, dest, destDy * width, minimapLineHeight * width);
			needed[lineIndex] = false;
			destDy += minimapLineHeight;
		}

		return needed;
	}

	private static bool IsFullWidth(int c) => (c >= 0x2E80 && c <= 0xD7AF) || (c >= 0xF900 && c <= 0xFAFF) || (c >= 0xFF01 && c <= 0xFF5E) || (c >= 0xFFE0 && c <= 0xFFE6);

	private void RenderLine(PixelBuffer target, Rgba8 background, int backgroundAlpha, bool useLighterFont, int charWidth, int foregroundAlpha, MinimapCharRenderer renderer, int dy, int innerLinePadding, int tabSize, TextView view, int modelLine, int fontScale, int minimapLineHeight) {
		if ((uint)modelLine >= (uint)view.Snapshot.LineCount) return;

		TextSpan span = view.Snapshot.GetLineSpan(modelLine);
		int length = view.LineColors(modelLine, ref _colors);

		if (_chars.Length < length) _chars = new char[Math.Max(length, _chars.Length * 2)];

		view.Snapshot.CopyTo(span.Start, length, _chars);

		int maxDx = target.Width - charWidth;
		bool force1pxHeight = minimapLineHeight == 1;
		int dx = (int)GutterWidth;
		int tabsCharDelta = 0;
		uint[] pixels = target.Pixels;

		for (int charIndex = 0; charIndex < length; charIndex++) {
			if (dx > maxDx) return;

			char charCode = _chars[charIndex];

			if (charCode == '\t') {
				int insertSpacesCount = tabSize - ((charIndex + tabsCharDelta) % tabSize);
				tabsCharDelta += insertSpacesCount - 1;
				dx += insertSpacesCount * charWidth;
			}
			else if (charCode == ' ') dx += charWidth;
			else {
				int repeat = IsFullWidth(charCode) ? 2 : 1;
				Rgba8 color = Rgba8.FromPacked(_colors[charIndex]);

				for (int i = 0; i < repeat; i++) {
					if (_options.Render == RenderKind.Blocks) renderer.BlockRenderChar(pixels, target.Width, target.Height, dx, dy + innerLinePadding, color, foregroundAlpha, background, backgroundAlpha, force1pxHeight);
					else renderer.RenderChar(pixels, target.Width, target.Height, dx, dy + innerLinePadding, charCode, color, foregroundAlpha, background, backgroundAlpha, fontScale, useLighterFont, force1pxHeight);

					dx += charWidth;

					if (dx > maxDx) return;
				}
			}
		}
	}

	private List<Decoration> CollectDecorations(TextView view) {
		List<Decoration> decorations = [];
		TextSnapshot snapshot = view.Snapshot;

		if (view.FindMatches is { Count: > 0 } matches) {
			for (int i = 0; i < matches.Count; i++) {
				FindMatch match = matches[i];
				int startLine = snapshot.GetLineAt(match.Start);
				int endLine = snapshot.GetLineAt(match.Start + match.Length);
				decorations.Add(new Decoration(startLine, match.Start - snapshot.GetLineStartOffset(startLine) + 1, endLine, match.Start + match.Length - snapshot.GetLineStartOffset(endLine) + 1, Theme.MinimapFindMatchHighlight, false, i == view.CurrentFindMatch ? 13 : 10));
			}
		}

		if (view.Squiggles is { Count: > 0 } squiggles) {
			foreach (TextView.Squiggle squiggle in squiggles) {
				(Color color, int zIndex)? style = squiggle.Severity switch {
					DiagnosticSeverity.Error => (Theme.MinimapErrorHighlight, 30),
					DiagnosticSeverity.Warning => (Theme.MinimapWarningHighlight, 20),
					DiagnosticSeverity.Info => (Theme.MinimapInfoHighlight, 10),
					_ => null,
				};

				if (style is { } s) decorations.Add(new Decoration(squiggle.Line, squiggle.StartColumn + 1, squiggle.EndLine, squiggle.EndColumn + 1, s.color, false, s.zIndex));
			}
		}

		foreach (DocumentHighlight highlight in Occurrences) decorations.Add(new Decoration(highlight.Line - 1, highlight.StartColumn, highlight.EndLine - 1, highlight.EndColumn, Theme.MinimapSelectionOccurrenceHighlight, false, 0));

		foreach (GitChange change in view.GitChanges()) {
			Color color = change.Kind switch {
				GitChangeKind.Added => Theme.MinimapGutterAddedBackground,
				GitChangeKind.Deleted => Theme.MinimapGutterDeletedBackground,
				_ => Theme.MinimapGutterModifiedBackground,
			};

			int start = change.Kind == GitChangeKind.Deleted ? Math.Max(0, change.Line) : change.Line;
			int end = change.Kind == GitChangeKind.Deleted ? start : change.Line + Math.Max(1, change.Count) - 1;
			decorations.Add(new Decoration(start, 1, end, 1, color, true, 0));
		}

		decorations.Sort(static (a, b) => a.ZIndex.CompareTo(b.ZIndex));
		return decorations;
	}

	private (int Start, int End) ToMinimapLines(int startModelLine, int endModelLine) {
		int start = ViewLineOf(Math.Clamp(startModelLine, 0, Math.Max(0, _snapshot!.LineCount - 1)));
		int end = ViewLineOf(Math.Clamp(endModelLine, 0, Math.Max(0, _snapshot.LineCount - 1)));

		if (_sampling is { } sampling) return sampling.DecorationRange(start, end);

		return (start, end);
	}

	private void RenderDecorations(IPainter painter, TextView view, Layout layout, float x, float y, float scale, float opacity) {
		float sx = _options.CanvasOuterWidth * scale / Math.Max(1, _options.CanvasInnerWidth);
		float sy = _options.CanvasOuterHeight * scale / Math.Max(1, _options.CanvasInnerHeight);
		int lineHeight = _options.MinimapLineHeight;
		int canvasWidth = _options.CanvasInnerWidth;
		int tabSize = view.TabWidth;
		float alpha = 0.9f * opacity;
		Dictionary<int, float[]> offsets = [];
		HashSet<int> highlighted = [];

		void Fill(float fx, float fy, float fw, float fh, Color color) {
			if (fw <= 0f || fh <= 0f) return;

			painter.FillRectangle(x + (fx * sx), y + (fy * sy), fw * sx, fh * sy, color.WithAlpha(color.A * alpha));
		}

		TextSpan selection = view.Selection;
		int selectionStartModel = view.Snapshot.GetLineAt(selection.Start);
		int selectionEndModel = view.Snapshot.GetLineAt(selection.End);
		(int selectionStart, int selectionEnd) = ToMinimapLines(selectionStartModel, selectionEndModel);
		int selectionStartColumn = selection.Start - view.Snapshot.GetLineStartOffset(selectionStartModel) + 1;
		int selectionEndColumn = selection.End - view.Snapshot.GetLineStartOffset(selectionEndModel) + 1;
		Color selectionColor = Theme.MinimapSelectionHighlight;

		if (selectionColor.A > 0f && layout.Intersect(selectionStart, selectionEnd) is var (s1, s2)) {
			for (int line = s1; line <= s2; line++) highlighted.Add(line);

			float y1 = layout.YForLineNumber(s1, lineHeight);
			float y2 = layout.YForLineNumber(s2, lineHeight);

			if (y2 > y1) Fill(GutterWidth, y1, canvasWidth, y2 - y1, selectionColor.WithAlpha(selectionColor.A * 0.5f));
		}

		List<Decoration> decorations = CollectDecorations(view);

		for (int i = decorations.Count - 1; i >= 0; i--) {
			Decoration decoration = decorations[i];

			if (decoration.Gutter) continue;

			(int start, int end) = ToMinimapLines(decoration.StartLine, decoration.EndLine);

			if (layout.Intersect(start, end) is not var (d1, d2) || decoration.Color.A <= 0f) continue;

			for (int line = d1; line <= d2; line++) {
				if (!highlighted.Add(line)) continue;

				Fill(GutterWidth, layout.YForLineNumber(line, lineHeight), canvasWidth, lineHeight, decoration.Color.WithAlpha(decoration.Color.A * 0.5f));
			}
		}

		if (selectionColor.A > 0f && layout.Intersect(selectionStart, selectionEnd) is var (r1, r2)) {
			for (int line = r1; line <= r2; line++) RenderDecorationOnLine(offsets, selectionStart, selectionStartColumn, selectionEnd, selectionEndColumn, selectionColor, layout, line, lineHeight, tabSize, canvasWidth, Fill);
		}

		foreach (Decoration decoration in decorations) {
			(int start, int end) = ToMinimapLines(decoration.StartLine, decoration.EndLine);

			if (layout.Intersect(start, end) is not var (d1, d2) || decoration.Color.A <= 0f) continue;

			for (int line = d1; line <= d2; line++) {
				if (decoration.Gutter) Fill(2, layout.YForLineNumber(line, lineHeight), GutterDecorationWidth, lineHeight, decoration.Color);
				else RenderDecorationOnLine(offsets, start, decoration.StartColumn, end, decoration.EndColumn, decoration.Color, layout, line, lineHeight, tabSize, canvasWidth, Fill);
			}
		}

		RenderSectionHeaders(painter, layout, x, y, sx, sy, alpha);
	}

	private delegate void FillAction(float x, float y, float width, float height, Color color);

	private void RenderDecorationOnLine(Dictionary<int, float[]> offsets, int startLine, int startColumn, int endLine, int endColumn, Color color, Layout layout, int line, int height, int tabSize, int canvasWidth, FillAction fill) {
		float y = layout.YForLineNumber(line, height);

		if (y + height < 0 || y > _options.CanvasInnerHeight) return;

		int from = startLine == line ? startColumn : 1;
		int to = endLine == line ? endColumn : MaxColumnOf(line);
		float x1 = XOffset(offsets, line, from, tabSize, canvasWidth);
		float x2 = XOffset(offsets, line, to, tabSize, canvasWidth);

		fill(x1, y, x2 - x1, height, color);
	}

	private int MaxColumnOf(int minimapLine) {
		int model = MinimapModelLine(minimapLine);
		return (uint)model < (uint)_snapshot!.LineCount ? _snapshot.GetLineSpan(model).Length + 1 : 1;
	}

	private float XOffset(Dictionary<int, float[]> offsets, int line, int column, int tabSize, int canvasWidth) {
		if (column == 1) return GutterWidth;

		float charWidth = _options.MinimapCharWidth;

		if ((column - 1) * charWidth >= canvasWidth) return canvasWidth;

		if (!offsets.TryGetValue(line, out float[]? map)) {
			int model = MinimapModelLine(line);
			string content = (uint)model < (uint)_snapshot!.LineCount ? _snapshot.GetText(_snapshot.GetLineSpan(model)) : string.Empty;
			List<float> values = [GutterWidth];
			float previous = GutterWidth;

			for (int i = 1; i < content.Length + 1; i++) {
				char c = content[i - 1];
				float delta = c == '\t' ? tabSize * charWidth : IsFullWidth(c) ? 2 * charWidth : charWidth;
				float next = previous + delta;

				if (next >= canvasWidth) {
					values.Add(canvasWidth);
					break;
				}

				values.Add(next);
				previous = next;
			}

			offsets[line] = map = [.. values];
		}

		return column - 1 < map.Length ? map[column - 1] : canvasWidth;
	}

	private void UpdateSectionHeaders(TextView view) {
		if (!_settings.ShowRegionSectionHeaders && !_settings.ShowMarkSectionHeaders) {
			_sectionHeaders = [];
			return;
		}

		int version = view.Buffer.Version;

		if (ReferenceEquals(view, _sectionHeadersView) && version == _sectionHeadersVersion) return;

		bool first = !ReferenceEquals(view, _sectionHeadersView) || _sectionHeadersVersion < 0;

		if (!first) {
			if (!_sectionHeadersDelay.IsRunning) {
				_sectionHeadersDelay.Restart();
				return;
			}

			if (_sectionHeadersDelay.ElapsedMilliseconds < 250) return;
		}

		_sectionHeadersDelay.Reset();
		_sectionHeadersView = view;
		_sectionHeadersVersion = version;
		_sectionHeaders = MinimapSectionHeaders.Find(view.Snapshot, view.Highlighter.Language.GrammarId, _settings.ShowRegionSectionHeaders, _settings.ShowMarkSectionHeaders, _settings.MarkSectionHeaderRegex, (line, column) => view.Highlighter.IsCommentAt(view.Snapshot, line, column));
	}

	private void RenderSectionHeaders(IPainter painter, Layout layout, float x, float y, float sx, float sy, float alpha) {
		if (_sectionHeaders.Count == 0) return;

		int minimapLineHeight = _options.MinimapLineHeight;
		float fontSize = _options.SectionHeaderFontSize;
		float letterSpacing = _options.SectionHeaderLetterSpacing;
		float backgroundFillHeight = fontSize * 1.5f;
		int canvasWidth = _options.CanvasInnerWidth;
		Color background = Theme.MinimapBackground;
		Color foreground = Theme.EditorForeground;
		ITextFont font = painter.GetUi(fontSize, false);
		float headerLines = fontSize / minimapLineHeight;
		int startLimit = (int)MathF.Floor(Math.Max(1, layout.StartLineNumber - headerLines));

		foreach (MinimapSectionHeader header in _sectionHeaders) {
			(int start, _) = ToMinimapLines(header.StartLine - 1, header.EndLine - 1);

			if (start < startLimit || start > layout.EndLineNumber) continue;

			float textY = layout.YForLineNumber(start, minimapLineHeight) + fontSize;
			float backgroundFillY = textY - fontSize;
			float separatorY = backgroundFillY + 2;
			string? text = header.Text.Length > 0 ? FitSectionHeader(font, header.Text, canvasWidth - GutterWidth, letterSpacing) : null;

			if (text is not null) {
				painter.FillRectangle(x, y + (backgroundFillY * sy), canvasWidth * sx, backgroundFillHeight * sy, background.WithAlpha(0.7f * alpha));
				DrawSpaced(painter, font, text, x + (GutterWidth * sx), y + (textY * sy), letterSpacing, foreground.WithAlpha(alpha));
			}

			if (header.HasSeparatorLine) painter.StrokeLine(x, y + (separatorY * sy), x + (canvasWidth * sx), y + (separatorY * sy), 0.4f, foreground.WithAlpha(alpha));
		}
	}

	private static float MeasureSpaced(ITextFont font, ReadOnlySpan<char> text, float letterSpacing) => font.Measure(text) + (text.Length * letterSpacing);

	private static void DrawSpaced(IPainter painter, ITextFont font, string text, float x, float baseline, float letterSpacing, Color color) {
		for (int i = 0; i < text.Length; i++) {
			painter.DrawText(font, text.AsSpan(i, 1), x, baseline, color);
			x += font.Measure(text.AsSpan(i, 1)) + letterSpacing;
		}
	}

	private string FitSectionHeader(ITextFont font, string text, float maxWidth, float letterSpacing) {
		if (_sectionHeaderCache.TryGetValue(text, out string? cached)) return cached;

		const string ellipsis = "…";
		float width = MeasureSpaced(font, text, letterSpacing);
		float ellipsisWidth = MeasureSpaced(font, ellipsis, letterSpacing);
		string result = text;

		if (width > maxWidth && width > ellipsisWidth) {
			float average = width / text.Length;
			int maxCharCount = (int)MathF.Floor((maxWidth - ellipsisWidth) / average) - 1;
			int half = (int)MathF.Ceiling(maxCharCount / 2f);

			while (half > 0 && char.IsWhiteSpace(text[half - 1])) half--;

			int tail = Math.Clamp(maxCharCount - half, 0, text.Length);
			result = text[..Math.Clamp(half, 0, text.Length)] + ellipsis + text[(text.Length - tail)..];
		}

		if (_sectionHeaderCache.Count >= 10) _sectionHeaderCache.Clear();

		_sectionHeaderCache[text] = result;
		return result;
	}

	public bool Contains(float absX, float absY, float height) => _settings.Enabled && Width > 0f && AcceptsPointer && absX >= _x && absX < _x + Width && absY >= _y && absY < _y + height;

	private bool OnSlider(float absY, float scale) => _layout is { SliderNeeded: true } layout && absY >= _y + (layout.SliderTop * scale) && absY < _y + ((layout.SliderTop + layout.SliderHeight) * scale);

	public bool Hover(float absX, float absY, float height, float scale) {
		bool over = Contains(absX, absY, height);
		bool slider = over && OnSlider(absY, scale);
		bool changed = over != _mouseOver || slider != _sliderHover;

		_mouseOver = over;
		_sliderHover = slider;
		return changed;
	}

	public void Leave() {
		_mouseOver = false;
		_sliderHover = false;
	}

	public bool IsDragging => _dragging;

	public bool Press(TextView view, float absX, float absY, float scale) {
		if (_layout is not { } layout || _options.Render == RenderKind.None) return false;

		if (_settings.Size != "proportional") {
			StartDrag(_y + ((layout.SliderTop + (layout.SliderHeight / 2f)) * scale), absX, layout);
			Drag(view, absX, absY, scale);
			return true;
		}

		if (OnSlider(absY, scale)) {
			StartDrag(absY, absX, layout);
			return true;
		}

		float internalOffsetY = _options.CanvasInnerHeight / _options.CanvasOuterHeight * ((absY - _y) / scale);
		int lineIndex = (int)MathF.Floor(internalOffsetY / _options.MinimapLineHeight);
		int lineNumber = Math.Min(lineIndex + layout.StartLineNumber - layout.TopPaddingLineCount, LineCount);
		int viewLine = _sampling is { } sampling ? sampling.Lines[Math.Clamp(lineNumber, 1, sampling.Lines.Length) - 1] : Math.Max(1, lineNumber);
		view.RevealLineInCenter(ModelLine(viewLine), view.LastLineHeight, view.LastViewportHeight);
		return true;
	}

	private void StartDrag(float initialY, float initialX, Layout layout) {
		_dragging = true;
		_dragStartY = initialY;
		_dragStartX = initialX;
		_dragLayout = layout;
	}

	public void Drag(TextView view, float absX, float absY, float scale) {
		if (!_dragging || _dragLayout is not { } layout) return;

		float orthogonal = Math.Min(Math.Abs(absX - _dragStartX), Math.Min(Math.Abs(absX - _x), Math.Abs(absX - _x - Width))) / scale;
		float scrollTop = orthogonal > PointerDragResetDistance ? layout.ScrollTop : layout.DesiredScrollTopFromDelta((absY - _dragStartY) / scale);

		view.SetScrollY(scrollTop * scale);
	}

	public void EndDrag() {
		_dragging = false;
		_dragLayout = null;
	}
}
