using Forge.Theming;

namespace Forge.Syntax;

public static class SyntaxColors
{
	private static readonly Color[] Table = Build();

	public static Color For(TokenKind kind) => Table[(int)kind];

	private static Color[] Build() {
		Color[] table = new Color[Enum.GetValues<TokenKind>().Length];

		table[(int)TokenKind.Text] = Theme.EditorForeground;

		table[(int)TokenKind.Keyword] = Color.Parse("#F97583");
		table[(int)TokenKind.ControlKeyword] = Color.Parse("#F97583");

		table[(int)TokenKind.Preprocessor] = Color.Parse("#F97583");

		table[(int)TokenKind.Type] = Color.Parse("#B392F0");
		table[(int)TokenKind.Function] = Color.Parse("#B392F0");

		table[(int)TokenKind.Constant] = Color.Parse("#79B8FF");

		table[(int)TokenKind.Variable] = Color.Parse("#79B8FF");

		table[(int)TokenKind.Parameter] = Theme.EditorForeground;
		table[(int)TokenKind.Number] = Color.Parse("#F8F8F8");

		table[(int)TokenKind.String] = Color.Parse("#FFAB70");

		table[(int)TokenKind.StringEscape] = Theme.EditorForeground;

		table[(int)TokenKind.Comment] = Color.Parse("#6B737C");
		table[(int)TokenKind.DocComment] = Color.Parse("#6B737C");

		table[(int)TokenKind.Operator] = Theme.EditorForeground;
		table[(int)TokenKind.Punctuation] = Theme.EditorForeground;

		table[(int)TokenKind.Tag] = Color.Parse("#FFAB70");

		table[(int)TokenKind.Attribute] = Color.Parse("#B392F0");

		table[(int)TokenKind.Invalid] = Color.Parse("#CD3131");

		return table;
	}
}
