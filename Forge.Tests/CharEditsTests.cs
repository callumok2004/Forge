using Forge.Text;

namespace Forge.Tests;

public class CharEditsTests
{
	private static string Apply(string original, string modified, List<CharChange> changes) {
		System.Text.StringBuilder result = new();
		int cursor = 0;

		foreach (CharChange change in changes) {
			Assert.True(change.OriginalStart >= cursor);
			result.Append(original, cursor, change.OriginalStart - cursor);
			result.Append(modified, change.ModifiedStart, change.ModifiedLength);
			cursor = change.OriginalStart + change.OriginalLength;
		}

		result.Append(original, cursor, original.Length - cursor);
		return result.ToString();
	}

	private static int Lcs(string left, string right) {
		int[,] table = new int[left.Length + 1, right.Length + 1];

		for (int i = 1; i <= left.Length; i++) {
			for (int j = 1; j <= right.Length; j++) table[i, j] = left[i - 1] == right[j - 1] ? table[i - 1, j - 1] + 1 : Math.Max(table[i - 1, j], table[i, j - 1]);
		}

		return table[left.Length, right.Length];
	}

	private static void AssertMinimal(string original, string modified) {
		List<CharChange> changes = CharEdits.Compute(original, modified);

		Assert.Equal(modified, Apply(original, modified, changes));

		int removed = changes.Sum(static change => change.OriginalLength);
		int inserted = changes.Sum(static change => change.ModifiedLength);
		int lcs = Lcs(original, modified);

		Assert.Equal(original.Length - lcs, removed);
		Assert.Equal(modified.Length - lcs, inserted);

		for (int i = 1; i < changes.Count; i++) Assert.True(changes[i].OriginalStart > changes[i - 1].OriginalStart + changes[i - 1].OriginalLength || changes[i].ModifiedStart > changes[i - 1].ModifiedStart + changes[i - 1].ModifiedLength);

		foreach (CharChange change in changes) Assert.True(change.OriginalLength > 0 || change.ModifiedLength > 0);
	}

	[Fact]
	public void IdenticalTextHasNoChanges() => Assert.Empty(CharEdits.Compute("same text", "same text"));

	[Fact]
	public void EmptyToEmptyHasNoChanges() => Assert.Empty(CharEdits.Compute("", ""));

	[Fact]
	public void EmptyToText() => Assert.Equal([new CharChange(0, 0, 0, 3)], CharEdits.Compute("", "abc"));

	[Fact]
	public void TextToEmpty() => Assert.Equal([new CharChange(0, 3, 0, 0)], CharEdits.Compute("abc", ""));

	[Fact]
	public void InsertionInTheMiddle() => Assert.Equal([new CharChange(1, 0, 1, 1)], CharEdits.Compute("ac", "abc"));

	[Fact]
	public void DeletionInTheMiddle() => Assert.Equal([new CharChange(1, 1, 1, 0)], CharEdits.Compute("abc", "ac"));

	[Fact]
	public void ReplacementInTheMiddle() => Assert.Equal([new CharChange(1, 1, 1, 1)], CharEdits.Compute("abc", "axc"));

	[Fact]
	public void PrefixOnlyChange() => Assert.Equal([new CharChange(0, 0, 0, 4)], CharEdits.Compute("int x;", "    int x;"));

	[Fact]
	public void SuffixOnlyChange() => Assert.Equal([new CharChange(6, 0, 6, 1)], CharEdits.Compute("int x;", "int x;\n"));

	[Fact]
	public void SeparateWhitespaceChangesStaySeparate() {
		List<CharChange> changes = CharEdits.Compute("int x=1;", "int x = 1;");

		Assert.Equal([new CharChange(5, 0, 5, 1), new CharChange(6, 0, 7, 1)], changes);
	}

	[Fact]
	public void ReindentedBlockKeepsCodeUntouched() {
		const string original = "{\nint a;\nint b;\n}";
		const string modified = "{\n    int a;\n    int b;\n}";

		List<CharChange> changes = CharEdits.Compute(original, modified);

		Assert.Equal([new CharChange(2, 0, 2, 4), new CharChange(9, 0, 13, 4)], changes);
	}

	[Fact]
	public void CrLfToLfIsOnlyCarriageReturnDeletions() {
		List<CharChange> changes = CharEdits.Compute("a\r\nb\r\nc", "a\nb\nc");

		Assert.All(changes, static change => Assert.Equal(0, change.ModifiedLength));
		Assert.Equal(2, changes.Sum(static change => change.OriginalLength));
	}

	[Fact]
	public void TabsToSpaces() => AssertMinimal("\tif (x)\n\t\treturn;", "    if (x)\n        return;");

	[Fact]
	public void SurrogatePairsDiffAsCodeUnits() => AssertMinimal("a😀b", "a😁b");

	[Fact]
	public void CompletelyDifferentText() => Assert.Equal([new CharChange(0, 3, 0, 3)], CharEdits.Compute("abc", "xyz"));

	[Fact]
	public void RepeatedCharacters() => AssertMinimal("aaaa", "aaaaaa");

	[Fact]
	public void RepeatedPatterns() => AssertMinimal("abababab", "babababa");

	[Fact]
	public void BraceMovedToNewLine() => AssertMinimal("void M() {\n}", "void M()\n{\n}");

	[Fact]
	public void BlankLinesCollapsed() => AssertMinimal("a;\n\n\n\nb;", "a;\n\nb;");

	[Fact]
	public void TrailingWhitespaceRemoved() => AssertMinimal("a;   \nb;\t\n", "a;\nb;\n");

	[Fact]
	public void VeryLargeDifferenceFallsBackToOneChange() {
		string original = new('a', 3000);
		string modified = new('b', 3000);

		List<CharChange> changes = CharEdits.Compute(original, modified);

		Assert.Equal([new CharChange(0, 3000, 0, 3000)], changes);
	}

	[Fact]
	public void LargeDifferenceStillReproducesTarget() {
		Random random = new(7);
		char[] left = new char[4000];
		char[] right = new char[4000];

		for (int i = 0; i < left.Length; i++) {
			left[i] = (char)('a' + random.Next(3));
			right[i] = (char)('a' + random.Next(3));
		}

		string original = new(left);
		string modified = new(right);

		Assert.Equal(modified, Apply(original, modified, CharEdits.Compute(original, modified)));
	}

	[Theory]
	[InlineData(1)]
	[InlineData(2)]
	[InlineData(3)]
	[InlineData(4)]
	[InlineData(5)]
	[InlineData(6)]
	[InlineData(7)]
	[InlineData(8)]
	public void RandomEditsAreMinimal(int seed) {
		Random random = new(seed);
		const string alphabet = "ab \n\t{}";

		for (int round = 0; round < 250; round++) {
			string original = RandomText(random, alphabet, random.Next(0, 24));
			string modified = Mutate(random, original, alphabet);

			AssertMinimal(original, modified);
		}
	}

	private static string RandomText(Random random, string alphabet, int length) {
		char[] buffer = new char[length];

		for (int i = 0; i < length; i++) buffer[i] = alphabet[random.Next(alphabet.Length)];

		return new string(buffer);
	}

	private static string Mutate(Random random, string text, string alphabet) {
		System.Text.StringBuilder builder = new(text);
		int operations = random.Next(0, 6);

		for (int i = 0; i < operations; i++) {
			int position = random.Next(builder.Length + 1);

			switch (random.Next(3)) {
				case 0:
					builder.Insert(position, alphabet[random.Next(alphabet.Length)]);
					break;

				case 1 when position < builder.Length:
					builder.Remove(position, 1);
					break;

				case 2 when position < builder.Length:
					builder[position] = alphabet[random.Next(alphabet.Length)];
					break;
			}
		}

		return builder.ToString();
	}
}
