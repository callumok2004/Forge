using Forge.Workspace;

namespace Forge.Tests;

public class DocumentSaveTests
{
	[Theory]
	[InlineData(new byte[] { (byte)'a', (byte)'\n', (byte)'b' })]
	[InlineData(new byte[] { (byte)'a', (byte)'\r', (byte)'\n', (byte)'b', (byte)'\r', (byte)'\n' })]
	[InlineData(new byte[] { 0xEF, 0xBB, 0xBF, (byte)'a', (byte)'\r', (byte)'\n' })]
	[InlineData(new byte[] { 0xFF, 0xFE, (byte)'a', 0, (byte)'\n', 0 })]
	public void SavingUnchangedFileKeepsBytes(byte[] content) {
		string path = Path.Combine(Path.GetTempPath(), $"forge-save-{Guid.NewGuid():N}.txt");

		try {
			File.WriteAllBytes(path, content);

			Assert.True(Document.Open(path).Save());
			Assert.Equal(content, File.ReadAllBytes(path));
		}
		finally {
			File.Delete(path);
		}
	}

	[Fact]
	public void UndoingBackToSavedStateIsClean() {
		Document document = Document.Untitled("x", "hello");

		document.Buffer.Insert(5, " world");
		Assert.True(document.IsDirty());

		document.Buffer.Undo();
		Assert.False(document.IsDirty());

		document.Buffer.Redo();
		Assert.True(document.IsDirty());
	}

	[Fact]
	public void TypingAfterSaveInSameUndoStepIsDirty() {
		string path = Path.Combine(Path.GetTempPath(), $"forge-save-{Guid.NewGuid():N}.txt");

		try {
			File.WriteAllText(path, string.Empty);
			Document document = Document.Open(path);

			document.Buffer.SetEditKind(document.Buffer.TypingKindFor('a'));
			document.Buffer.Insert(0, "a");
			Assert.True(document.Save());

			document.Buffer.SetEditKind(document.Buffer.TypingKindFor('b'));
			document.Buffer.Insert(1, "b");
			Assert.True(document.IsDirty());
		}
		finally {
			File.Delete(path);
		}
	}
}
