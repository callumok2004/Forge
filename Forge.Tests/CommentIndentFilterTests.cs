using Forge.Extensions.Dotnet.Language;
using Forge.Extensions.Dotnet.Server;

namespace Forge.Tests;

public class CommentIndentFilterTests
{
	[Fact]
	public void KeepsIndentOfCommentAfterLineBreak() {
		string text = "int a; // trailing\n        // aligned by hand\nint b;";
		ServerTextEdit edit = new(1, 19, 2, 9, "\n       ");

		List<ServerTextEdit> result = CommentIndentFilter.Apply(text, [edit]);

		Assert.Single(result);
		Assert.Equal("\n        ", result[0].NewText);
	}

	[Fact]
	public void DropsInPlaceReindentOfCommentLine() {
		string text = "class C\n{\n      // note\n}";
		ServerTextEdit edit = new(3, 1, 3, 7, "    ");

		Assert.Empty(CommentIndentFilter.Apply(text, [edit]));
	}

	[Fact]
	public void LeavesCodeEditsAlone() {
		string text = "class C\n{\n      int x;\n}";
		ServerTextEdit edit = new(3, 1, 3, 7, "    ");

		Assert.Equal([edit], CommentIndentFilter.Apply(text, [edit]));
	}
}
