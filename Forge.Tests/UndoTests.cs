using Forge.Text;

namespace Forge.Tests;

public class UndoTests
{
	[Fact]
	public void UndoInsert() {
		TextBuffer buffer = new("hello");
		buffer.Insert(5, " world");

		Assert.True(buffer.Undo());
		Assert.Equal("hello", buffer.GetText());
		buffer.Validate();
	}

	[Fact]
	public void UndoDelete() {
		TextBuffer buffer = new("hello world");
		buffer.Delete(5, 6);

		Assert.True(buffer.Undo());
		Assert.Equal("hello world", buffer.GetText());
		buffer.Validate();
	}

	[Fact]
	public void UndoReplace() {
		TextBuffer buffer = new("hello world");
		buffer.Replace(6, 5, "forge");

		Assert.Equal("hello forge", buffer.GetText());
		Assert.True(buffer.Undo());
		Assert.Equal("hello world", buffer.GetText());
		buffer.Validate();
	}

	[Fact]
	public void RedoReappliesTheEdit() {
		TextBuffer buffer = new("hello");
		buffer.Insert(5, " world");
		buffer.Undo();

		Assert.True(buffer.Redo());
		Assert.Equal("hello world", buffer.GetText());
		buffer.Validate();
	}

	[Fact]
	public void UndoUnwindsInReverseOrder() {
		TextBuffer buffer = new();
		buffer.Insert(0, "a");
		buffer.Insert(1, "b");
		buffer.Insert(2, "c");

		Assert.Equal("abc", buffer.GetText());

		buffer.Undo();
		Assert.Equal("ab", buffer.GetText());
		buffer.Undo();
		Assert.Equal("a", buffer.GetText());
		buffer.Undo();
		Assert.Equal(string.Empty, buffer.GetText());
		Assert.False(buffer.Undo());
	}

	[Fact]
	public void EditGroupUndoesAsOneStep() {
		TextBuffer buffer = new("one two three");

		buffer.BeginEdit();
		buffer.Replace(0, 3, "1");
		buffer.Replace(2, 3, "2");
		buffer.EndEdit();

		Assert.Equal("1 2 three", buffer.GetText());

		Assert.True(buffer.Undo());
		Assert.Equal("one two three", buffer.GetText());
		Assert.False(buffer.Undo());
		buffer.Validate();
	}

	[Fact]
	public void EditGroupRedoesAsOneStep() {
		TextBuffer buffer = new("one two three");

		buffer.BeginEdit();
		buffer.Replace(0, 3, "1");
		buffer.Replace(2, 3, "2");
		buffer.EndEdit();
		buffer.Undo();

		Assert.True(buffer.Redo());
		Assert.Equal("1 2 three", buffer.GetText());
		buffer.Validate();
	}

	[Fact]
	public void NestedEditGroupsCollapseToTheOutermost() {
		TextBuffer buffer = new("abc");

		buffer.BeginEdit();
		buffer.Insert(0, "1");
		buffer.BeginEdit();
		buffer.Insert(0, "2");
		buffer.EndEdit();
		buffer.Insert(0, "3");
		buffer.EndEdit();

		Assert.Equal("321abc", buffer.GetText());
		Assert.True(buffer.Undo());
		Assert.Equal("abc", buffer.GetText());
		Assert.False(buffer.Undo());
	}

	[Fact]
	public void NewEditClearsTheRedoStack() {
		TextBuffer buffer = new("abc");
		buffer.Insert(3, "d");
		buffer.Undo();

		Assert.True(buffer.CanRedo);

		buffer.Insert(0, "z");
		Assert.False(buffer.CanRedo);
	}

	[Fact]
	public void VersionAdvancesOnEveryMutation() {
		TextBuffer buffer = new("abc");
		int start = buffer.Version;

		buffer.Insert(0, "x");
		Assert.Equal(start + 1, buffer.Version);

		buffer.Undo();
		Assert.Equal(start + 2, buffer.Version);
	}
}
