using System.Text.RegularExpressions;

namespace Forge.Syntax.TextMate;

public sealed class TieredRegex
{
	private const int PromoteAfter = 32;

	private const RegexOptions Options = RegexOptions.Multiline | RegexOptions.CultureInvariant;

	private static readonly Dictionary<string, TieredRegex?> Cache = [with(StringComparer.Ordinal)];
	private static readonly Lock Sync = new();

	private Regex _current;
	private int _uses;

	private TieredRegex(Regex regex, bool final) {
		_current = regex;
		_uses = final ? PromoteAfter : 0;
		AnchorsAtStart = regex.ToString().Contains("\\G", StringComparison.Ordinal);
	}

	public static TimeSpan StartDelay { get; set; } = TimeSpan.FromSeconds(2);

	public bool AnchorsAtStart { get; }

	public bool IsBundled => _uses == PromoteAfter && _current.GetType() != typeof(Regex);

	public Regex Use() {
		if (_uses < PromoteAfter && ++_uses == PromoteAfter) Promoter.Enqueue(this);

		return Volatile.Read(ref _current);
	}

	public static TieredRegex? For(string pattern) {
		lock (Sync) {
			if (Cache.TryGetValue(pattern, out TieredRegex? cached)) return cached;
		}

		TieredRegex? compiled = Create(OnigTranslate.Translate(pattern));

		lock (Sync) Cache[pattern] = compiled;

		return compiled;
	}

	public static TieredRegex? Create(string translated) {
		if (BundledRegexes.For(translated) is { } bundled) return new TieredRegex(bundled, final: true);

		try {
			return new TieredRegex(new Regex(translated, Options), final: false);
		}
		catch (ArgumentException) {
			return null;
		}
	}

	private void Promote() => Volatile.Write(ref _current, new Regex(_current.ToString(), Options | RegexOptions.Compiled));

	private static class Promoter
	{
		private static readonly System.Collections.Concurrent.BlockingCollection<TieredRegex> Queue = [];

		private static readonly Lazy<Thread> Worker = new(static () => {
			Thread thread = new(Run) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Forge grammar compiler" };
			thread.Start();
			return thread;
		});

		public static void Enqueue(TieredRegex regex) {
			Queue.Add(regex);
			_ = Worker.Value;
		}

		private static void Run() {
			Thread.Sleep(StartDelay);

			foreach (TieredRegex regex in Queue.GetConsumingEnumerable()) regex.Promote();
		}
	}
}

internal static partial class BundledRegexes
{
	public static Regex? For(string translated) {
		Regex? regex = null;
		Find(translated, ref regex);
		return regex;
	}

	static partial void Find(string translated, ref Regex? regex);
}
