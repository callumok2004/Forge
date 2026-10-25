using Forge.Editor;
using Forge.Text;

namespace Forge.Tests;

public class UndoStopTests
{
	[Fact]
	public void CursorMoveSplitsTypingIntoSeparateSteps() {
		TextView view = new(new TextBuffer(string.Empty));

		view.InsertText("a");
		view.InsertText("b");
		view.PushUndoStop();
		view.MoveTo(0, extend: false);
		view.InsertText("c");

		view.Undo();
		Assert.Equal("ab", view.Buffer.GetText());

		view.Undo();
		Assert.Equal(string.Empty, view.Buffer.GetText());
	}

	[Fact]
	public void ConsecutiveTypingIsOneStep() {
		TextView view = new(new TextBuffer(string.Empty));

		view.InsertText("a");
		view.InsertText("b");
		view.InsertText("c");

		view.Undo();
		Assert.Equal(string.Empty, view.Buffer.GetText());
	}

	[Fact]
	public void UndoRestoresSelectionBeforeEditAndRedoRestoresAfter() {
		TextView view = new(new TextBuffer("hello world"));

		view.MoveTo(6, extend: false);
		view.MoveTo(11, extend: true);
		view.InsertText("x");

		view.Undo();
		Assert.Equal("hello world", view.Buffer.GetText());
		Assert.Equal(6, view.SelectionAnchor);
		Assert.Equal(11, view.CaretOffset);

		view.Redo();
		Assert.Equal("hello x", view.Buffer.GetText());
		Assert.Equal(7, view.CaretOffset);
		Assert.False(view.HasSelection);
	}
}
