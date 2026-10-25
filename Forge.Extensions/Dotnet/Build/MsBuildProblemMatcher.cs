using Forge.Common.Commands;
using Forge.Common.Extensions;
using Forge.Core;
using Forge.Extensions.Host;

namespace Forge.Extensions.Dotnet.Build;

public sealed class MsBuildProblemMatcher
{
	private readonly List<Diagnostic> _diagnostics = [];
	private readonly HashSet<string> _seen = [with(StringComparer.Ordinal)];

	public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics;

	public int ErrorCount { get; private set; }

	public int WarningCount { get; private set; }

	public void Clear() {
		_diagnostics.Clear();
		_seen.Clear();
		ErrorCount = 0;
		WarningCount = 0;
	}

	public bool Read(string line) {
		if (Parse(line) is not { } diagnostic) return false;

		string key = $"{diagnostic.File}|{diagnostic.Line}|{diagnostic.Column}|{diagnostic.Message}";

		if (!_seen.Add(key)) return true;

		_diagnostics.Add(diagnostic);

		if (diagnostic.IsError) ErrorCount++;
		else WarningCount++;

		return true;
	}

	public static Diagnostic? Parse(string line) {
		ReadOnlySpan<char> text = line.AsSpan().TrimStart();

		int close = text.IndexOf("): ".AsSpan());

		if (close < 0) return null;

		ReadOnlySpan<char> location = text[..close];
		ReadOnlySpan<char> rest = text[(close + 3)..].TrimStart();

		bool isError;

		if (rest.StartsWith("error ".AsSpan())) {
			isError = true;
			rest = rest[6..];
		}
		else if (rest.StartsWith("warning ".AsSpan())) {
			isError = false;
			rest = rest[8..];
		}
		else if (rest.StartsWith("info ".AsSpan())) {
			isError = false;
			rest = rest[5..];
		}
		else return null;

		rest = rest.TrimStart();

		int codeEnd = rest.IndexOf(": ".AsSpan());

		if (codeEnd <= 0 || !IsDiagnosticCode(rest[..codeEnd])) return null;

		ReadOnlySpan<char> code = rest[..codeEnd];
		ReadOnlySpan<char> message = rest[(codeEnd + 2)..].TrimEnd();

		if (message.EndsWith("]".AsSpan())) {
			int bracket = message.LastIndexOf(" [".AsSpan());

			if (bracket > 0) message = message[..bracket];
		}

		if (!ParseLocation(location, out string file, out int lineNumber, out int column)) return null;

		return new Diagnostic(file, lineNumber, column, $"{code}: {message}", isError);
	}

	private static bool IsDiagnosticCode(ReadOnlySpan<char> code) {
		int index = 0;

		while (index < code.Length && char.IsAsciiLetter(code[index])) index++;

		if (index == 0 || index == code.Length) return false;

		for (int i = index; i < code.Length; i++) {
			if (!char.IsAsciiDigit(code[i])) return false;
		}

		return true;
	}

	private static bool ParseLocation(ReadOnlySpan<char> location, out string file, out int line,
		out int column) {
		file = string.Empty;
		line = 0;
		column = 0;

		int open = location.LastIndexOf('(');

		if (open <= 0) return false;

		ReadOnlySpan<char> position = location[(open + 1)..];
		int comma = position.IndexOf(',');

		if (comma < 0 ||
			!int.TryParse(position[..comma], out line) ||
			!int.TryParse(position[(comma + 1)..], out column)) return false;

		file = location[..open].Trim().ToString();
		return file.Length > 0;
	}
}
