using System.Collections.Concurrent;
using System.Text;

namespace Forge.Benchmarks;

internal static class DocumentSource
{
	private const int PoolSize = 2048;

	private static readonly ConcurrentDictionary<(int Lines, int Seed), string> Cache = new();

	public static string Code(int lines, int seed = 1) =>
		Cache.GetOrAdd((lines, seed), static key => Generate(key.Lines, key.Seed));

	public static byte[] Utf8(int lines, int seed = 1) => Encoding.UTF8.GetBytes(Code(lines, seed));

	private static string Generate(int lines, int seed) {
		Random random = new(seed);

		string[] pool = new string[Math.Min(PoolSize, Math.Max(lines, 1))];
		for (int i = 0; i < pool.Length; i++) pool[i] = GenerateLine(random);

		StringBuilder builder = new(lines * 48);
		for (int i = 0; i < lines; i++) {
			builder.Append(pool[i % pool.Length]);
			builder.Append('\n');
		}

		return builder.ToString();
	}

	private static string GenerateLine(Random random) {
		StringBuilder line = new(48);
		line.Append('\t', random.Next(0, 4));

		int words = random.Next(3, 10);
		for (int word = 0; word < words; word++) {
			if (word > 0) line.Append(' ');

			int length = random.Next(2, 12);
			for (int c = 0; c < length; c++) line.Append((char)('a' + random.Next(26)));
		}

		return line.ToString();
	}
}
