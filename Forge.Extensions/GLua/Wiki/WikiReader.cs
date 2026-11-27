using System.Text.Json;

namespace Forge.Extensions.GLua.Wiki;

public sealed class WikiData
{
	public required Dictionary<string, Dictionary<string, WikiDoc>> Sections { get; init; }
}

public static class WikiReader
{
	public static WikiData Load(string path) {
		byte[] bytes = File.ReadAllBytes(path);
		WikiSource source = new(bytes);
		Dictionary<string, string> pool = [with(StringComparer.Ordinal)];
		Utf8JsonReader reader = new(bytes, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
		Dictionary<string, Dictionary<string, WikiDoc>> sections = [with(StringComparer.Ordinal)];

		reader.Read();

		while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName) {
			string section = reader.GetString()!;
			reader.Read();
			sections[section] = ReadMembers(ref reader, source, pool);
		}

		return new WikiData { Sections = sections };
	}

	private static Dictionary<string, WikiDoc> ReadMembers(ref Utf8JsonReader reader, WikiSource source, Dictionary<string, string> pool) {
		Dictionary<string, WikiDoc> members = [with(StringComparer.Ordinal)];

		if (reader.TokenType != JsonTokenType.StartObject) {
			reader.Skip();
			return members;
		}

		while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName) {
			string name = Intern(pool, reader.GetString()!);
			reader.Read();

			if (reader.TokenType == JsonTokenType.StartObject) members[name] = ReadDoc(ref reader, source, pool);
			else reader.Skip();
		}

		return members;
	}

	private static string Intern(Dictionary<string, string> pool, string value) {
		if (pool.TryGetValue(value, out string? existing)) return existing;

		pool[value] = value;
		return value;
	}

	private static WikiText Text(ref Utf8JsonReader reader, WikiSource source) {
		if (reader.TokenType != JsonTokenType.String) {
			reader.Skip();
			return default;
		}

		return new WikiText(source, (int)reader.TokenStartIndex, reader.ValueSpan.Length + 2);
	}

	private static WikiText[] Texts(ref Utf8JsonReader reader, WikiSource source) {
		if (reader.TokenType != JsonTokenType.StartArray) {
			reader.Skip();
			return [];
		}

		List<WikiText> texts = [];

		while (reader.Read() && reader.TokenType != JsonTokenType.EndArray) texts.Add(Text(ref reader, source));

		return [.. texts];
	}

	private static WikiDoc[] Docs(ref Utf8JsonReader reader, WikiSource source, Dictionary<string, string> pool) {
		if (reader.TokenType != JsonTokenType.StartArray) {
			reader.Skip();
			return [];
		}

		List<WikiDoc> docs = [];

		while (reader.Read() && reader.TokenType != JsonTokenType.EndArray) {
			if (reader.TokenType == JsonTokenType.StartObject) docs.Add(ReadDoc(ref reader, source, pool));
			else reader.Skip();
		}

		return [.. docs];
	}

	private static WikiBug[] Bugs(ref Utf8JsonReader reader, WikiSource source, Dictionary<string, string> pool) {
		if (reader.TokenType != JsonTokenType.StartArray) {
			reader.Skip();
			return [];
		}

		List<WikiBug> bugs = [];

		while (reader.Read() && reader.TokenType != JsonTokenType.EndArray) {
			if (reader.TokenType != JsonTokenType.StartObject) {
				reader.Skip();
				continue;
			}

			string? issue = null;
			string? pull = null;
			WikiText description = default;

			while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName) {
				string key = reader.GetString()!;
				reader.Read();

				switch (key) {
					case "ISSUE": issue = Scalar(ref reader, pool); break;
					case "PULL": pull = Scalar(ref reader, pool); break;
					case "DESCRIPTION": description = Text(ref reader, source); break;
					default: reader.Skip(); break;
				}
			}

			bugs.Add(new WikiBug { Issue = issue, Pull = pull, Description = description });
		}

		return [.. bugs];
	}

	private static string? Scalar(ref Utf8JsonReader reader, Dictionary<string, string> pool) {
		switch (reader.TokenType) {
			case JsonTokenType.String: return Intern(pool, reader.GetString()!);
			case JsonTokenType.Number: return Intern(pool, System.Text.Encoding.UTF8.GetString(reader.ValueSpan));
			default:
				reader.Skip();
				return null;
		}
	}

	private static WikiDoc ReadDoc(ref Utf8JsonReader reader, WikiSource source, Dictionary<string, string> pool) {
		WikiFlags flags = WikiFlags.None;
		string? search = null, link = null, value = null, family = null, enumName = null, type = null, name = null, defaultValue = null, parent = null;
		WikiText description = default, baseDescription = default;
		WikiText[]? notes = null, warnings = null;
		WikiBug[]? bugs = null;
		WikiDoc[]? arguments = null, callback = null, returns = null;
		(string, string)? src = null;
		Dictionary<string, WikiDoc>? members = null;

		while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName) {
			string key = reader.GetString()!;
			reader.Read();

			switch (key) {
				case "SEARCH": search = Scalar(ref reader, pool); break;
				case "LINK": link = Scalar(ref reader, pool); break;
				case "VALUE": value = Scalar(ref reader, pool); break;
				case "FAMILY": family = Scalar(ref reader, pool); break;
				case "ENUM": enumName = Scalar(ref reader, pool); break;
				case "TYPE": type = Scalar(ref reader, pool); break;
				case "NAME": name = Scalar(ref reader, pool); break;
				case "DEFAULT": defaultValue = Scalar(ref reader, pool); break;
				case "PARENT": parent = Scalar(ref reader, pool); break;
				case "DESCRIPTION": description = Text(ref reader, source); break;
				case "BASE_DESCRIPTION": baseDescription = Text(ref reader, source); break;
				case "NOTES": notes = Texts(ref reader, source); break;
				case "WARNINGS": warnings = Texts(ref reader, source); break;
				case "BUGS": bugs = Bugs(ref reader, source, pool); break;
				case "ARGUMENTS": arguments = Docs(ref reader, source, pool); break;
				case "CALLBACK": callback = Docs(ref reader, source, pool); break;
				case "RETURNS": returns = Docs(ref reader, source, pool); break;
				case "MEMBERS": members = ReadMembers(ref reader, source, pool); break;
				case "SRC":
					if (reader.TokenType == JsonTokenType.StartArray) {
						reader.Read();
						string? file = Scalar(ref reader, pool);
						reader.Read();
						string? lines = Scalar(ref reader, pool);

						while (reader.TokenType != JsonTokenType.EndArray && reader.Read()) {
						}

						if (file is not null && lines is not null) src = (file, lines);
					}
					else reader.Skip();

					break;
				case "CLIENT": flags |= WikiFlags.HasClient | (IsTrue(ref reader) ? WikiFlags.Client : 0); break;
				case "MENU": flags |= WikiFlags.HasMenu | (IsTrue(ref reader) ? WikiFlags.Menu : 0); break;
				case "SERVER": flags |= WikiFlags.HasServer | (IsTrue(ref reader) ? WikiFlags.Server : 0); break;
				case "NEW": reader.Skip(); flags |= WikiFlags.New; break;
				case "DEPRECATED": reader.Skip(); flags |= WikiFlags.Deprecated; break;
				case "INTERNAL": reader.Skip(); flags |= WikiFlags.Internal; break;
				case "REF_ONLY": reader.Skip(); flags |= WikiFlags.ReferenceOnly; break;
				case "PREDICTED": reader.Skip(); flags |= WikiFlags.Predicted; break;
				case "EVENT": flags |= IsTrue(ref reader) ? WikiFlags.Event : 0; break;
				case "FUNCTION": reader.Skip(); flags |= WikiFlags.Function; break;
				default: reader.Skip(); break;
			}
		}

		return new WikiDoc {
			Search = search, Flags = flags, Link = link, Value = value, Family = family, Enum = enumName, Type = type, Name = name, Default = defaultValue, Parent = parent,
			Description = description, BaseDescription = baseDescription, Notes = notes, Warnings = warnings, Bugs = bugs,
			Arguments = arguments, Callback = callback, Returns = returns, Source = src, Members = members,
		};
	}

	private static bool IsTrue(ref Utf8JsonReader reader) {
		bool result = reader.TokenType == JsonTokenType.True;

		if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) reader.Skip();

		return result;
	}
}
