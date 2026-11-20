using Forge.Common.Render;
using Forge.Language;
using Forge.Theming;
using Forge.UI.Controls;

namespace Forge.Tests;

public sealed class TextSelectionTests
{
	private sealed class FakeFont : ITextFont
	{
		public string GetFamilyName() => "Fake";
		public float GetSizeInPixels() => 10f;
		public float GetCellWidth() => 10f;
		public float GetLineHeight() => 10f;
		public float GetBaseline() => 8f;
		public float GetAscent() => 8f;
		public float GetDescent() => 2f;
		public bool IsMonospace() => true;
		public ushort GetGlyphIndex(char character) => 1;
		public float GetAdvance(char character) => 10f;
		public float Measure(ReadOnlySpan<char> text) => text.Length * 10f;
	}

	private static readonly FakeFont Font = new();

	private static TextSelection Wrapped(string text, float width, out List<string> pieces) {
		pieces = [];
		SuggestDetails.Wrap(Font, text, width, pieces);
		TextSelection selection = new();

		for (int i = 0; i < pieces.Count; i++) selection.Add(new TextRun(0f, i * 10f, pieces[i], Font, Theme.Foreground, i > 0));

		return selection;
	}

	[Fact]
	public void WrappedPiecesRejoinToTheOriginalText() {
		const string text = "the quick brown fox jumps over the lazy dog";
		_ = Wrapped(text, 100f, out List<string> pieces);

		Assert.True(pieces.Count > 1);
		Assert.Equal(text, string.Concat(pieces));
	}

	[Fact]
	public void TripleClickSelectsTheWholeWrappedLine() {
		const string text = "the quick brown fox jumps over the lazy dog";
		TextSelection selection = Wrapped(text, 100f, out _);

		selection.SelectLine(5f, 15f);

		Assert.Equal(text, selection.SelectedText());
	}

	[Fact]
	public void DoubleClickSelectsAWordAcrossAWrap() {
		TextSelection selection = new();
		selection.Add(new TextRun(0f, 0f, "Identif", Font, Theme.Foreground, false));
		selection.Add(new TextRun(0f, 10f, "ierName rest", Font, Theme.Foreground, true));

		selection.SelectWord(5f, 15f);

		Assert.Equal("IdentifierName", selection.SelectedText());
	}
}
