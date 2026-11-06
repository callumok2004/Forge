using Forge.Editor;
using Forge.Text;

namespace Forge.Tests;

public class UndoSaveTests
{
	private static void Type(TextView view, string text) {
		foreach (char character in text) view.InsertText([character]);
	}

	private static void TrimOnSave(TextView view) {
		string current = view.Buffer.GetText();
		string trimmed = string.Join("\n", current.Split('\n').Select(static line => line.TrimEnd(' ', '\t')));

		if (trimmed != current) view.Buffer.ReplaceChanged(current, trimmed);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void UndoAfterSavesWalksBackEachEdit(bool save) {
		TextView view = new(new TextBuffer("class A\n"));
		view.MoveTo(7, extend: false);

		view.InsertNewLine();
		if (save) TrimOnSave(view);
		Type(view, "asd");
		if (save) TrimOnSave(view);
		view.InsertNewLine();
		if (save) TrimOnSave(view);
		Type(view, "asd");
		if (save) TrimOnSave(view);

		Assert.Equal("class A\nasd\nasd\n", view.Buffer.GetText());

		view.Undo();
		Assert.Equal("class A\nasd\n", view.Buffer.GetText());
		Assert.Equal(11, view.CaretOffset);

		view.Undo();
		Assert.Equal("class A\n", view.Buffer.GetText());
		Assert.Equal(7, view.CaretOffset);

		Assert.False(view.Buffer.CanUndo);
	}

	[Fact]
	public void IndentedEnterJoinsFollowingTyping() {
		TextView view = new(new TextBuffer("\tclass A\n"));
		view.MoveTo(8, extend: false);

		view.InsertNewLine();
		Type(view, "asd");
		view.InsertNewLine();
		Type(view, "asd");

		Assert.Equal("\tclass A\n\tasd\n\tasd\n", view.Buffer.GetText());

		view.Undo();
		Assert.Equal("\tclass A\n\tasd\n", view.Buffer.GetText());

		view.Undo();
		Assert.Equal("\tclass A\n", view.Buffer.GetText());
		Assert.False(view.Buffer.CanUndo);
	}

	[Fact]
	public void AutoClosedPairJoinsFollowingTyping() {
		TextView view = new(new TextBuffer("x"));
		view.MoveTo(1, extend: false);

		Type(view, "ab");
		Assert.True(view.TryAutoClose('('));
		Type(view, "cd");

		Assert.Equal("xab(cd)", view.Buffer.GetText());

		view.Undo();
		Assert.Equal("xab", view.Buffer.GetText());

		view.Undo();
		Assert.Equal("x", view.Buffer.GetText());
	}

	[Fact]
	public void TypingAfterUndoKeepsHistoryConsistent() {
		TextView view = new(new TextBuffer("start\n"));
		view.MoveTo(5, extend: false);

		for (int round = 0; round < 3; round++) {
			for (int i = 0; i < 4; i++) {
				view.InsertNewLine();
				Type(view, "asd");
			}

			TrimOnSave(view);

			while (view.Buffer.CanUndo) view.Undo();

			Assert.Equal("start\n", view.Buffer.GetText());
			Assert.Equal(5, view.CaretOffset);
		}

		view.InsertNewLine();
		Type(view, "asd");
		view.Undo();
		Type(view, "x");
		view.Undo();
		Assert.Equal("start\n", view.Buffer.GetText());

		view.MoveTo(0, extend: false);
		view.Cut();
		Assert.Equal(string.Empty, view.Buffer.GetText());

		view.Undo();
		Assert.Equal("start\n", view.Buffer.GetText());
	}

	[Fact]
	public void EnterIsItsOwnUndoStepAfterSaveTrimsIndent() {
		TextView view = new(new TextBuffer("\tclass A\n"));
		view.MoveTo(8, extend: false);

		view.InsertNewLine();
		TrimOnSave(view);
		Type(view, "asd");
		TrimOnSave(view);
		view.InsertNewLine();
		TrimOnSave(view);
		Type(view, "asd");
		TrimOnSave(view);

		Assert.Equal("\tclass A\nasd\nasd\n", view.Buffer.GetText());

		view.Undo();
		Assert.Equal("\tclass A\nasd\n", view.Buffer.GetText());
		Assert.Equal(12, view.CaretOffset);

		view.Undo();
		Assert.Equal("\tclass A\n\n", view.Buffer.GetText());

		view.Undo();
		Assert.Equal("\tclass A\n\t\n", view.Buffer.GetText());

		view.Undo();
		Assert.Equal("\tclass A\n", view.Buffer.GetText());
		Assert.Equal(8, view.CaretOffset);
	}
}
