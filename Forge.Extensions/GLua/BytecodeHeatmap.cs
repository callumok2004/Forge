using System.Globalization;
using System.Text;

using Forge.Common.Commands;
using Forge.Theming;

namespace Forge.Extensions.GLua;

public static class BytecodeHeatmap
{
	private const string LuaJitBytecodeUrl = "http://wiki.luajit.org/Bytecode-2.0";
	private const string ClosureUrl = "https://www.lua.org/pil/6.1.html";
	private const string Names21 = "ISLT  ISGE  ISLE  ISGT  ISEQV ISNEV ISEQS ISNES ISEQN ISNEN ISEQP ISNEP ISTC  ISFC  IST   ISF   ISTYPEISNUM MOV   NOT   UNM   LEN   ADDVN SUBVN MULVN DIVVN MODVN ADDNV SUBNV MULNV DIVNV MODNV ADDVV SUBVV MULVV DIVVV MODVV POW   CAT   KSTR  KCDATAKSHORTKNUM  KPRI  KNIL  UGET  USETV USETS USETN USETP UCLO  FNEW  TNEW  TDUP  GGET  GSET  TGETV TGETS TGETB TGETR TSETV TSETS TSETB TSETM TSETR CALLM CALL  CALLMTCALLT ITERC ITERN VARG  ISNEXTRETM  RET   RET0  RET1  FORI  JFORI FORL  IFORL JFORL ITERL IITERLJITERLLOOP  ILOOP JLOOP JMP   FUNCF IFUNCFJFUNCFFUNCV IFUNCVJFUNCVFUNCC FUNCCW";
	private const string Names20 = "ISLT  ISGE  ISLE  ISGT  ISEQV ISNEV ISEQS ISNES ISEQN ISNEN ISEQP ISNEP ISTC  ISFC  IST   ISF   MOV   NOT   UNM   LEN   ADDVN SUBVN MULVN DIVVN MODVN ADDNV SUBNV MULNV DIVNV MODNV ADDVV SUBVV MULVV DIVVV MODVV POW   CAT   KSTR  KCDATAKSHORTKNUM  KPRI  KNIL  UGET  USETV USETS USETN USETP UCLO  FNEW  TNEW  TDUP  GGET  GSET  TGETV TGETS TGETB TSETV TSETS TSETB TSETM CALLM CALL  CALLMTCALLT ITERC ITERN VARG  ISNEXTRETM  RET   RET0  RET1  FORI  JFORI FORL  IFORL JFORL ITERL IITERLJITERLLOOP  ILOOP JLOOP JMP   FUNCF IFUNCFJFUNCFFUNCV IFUNCVJFUNCVFUNCC FUNCCW";

	public static readonly Dictionary<string, double> Weights = new(StringComparer.Ordinal) {
		["ISLT"] = 1, ["ISGE"] = 1, ["ISLE"] = 1, ["ISGT"] = 1, ["ISEQV"] = 1, ["ISNEV"] = 1, ["ISEQS"] = 1, ["ISNES"] = 1, ["ISEQN"] = 1, ["ISNEN"] = 1, ["ISEQP"] = 1, ["ISNEP"] = 1,
		["ISTC"] = 1, ["ISFC"] = 1, ["IST"] = 1, ["ISF"] = 1, ["ISTYPE"] = 1, ["ISNUM"] = 1, ["MOV"] = 0.3, ["NOT"] = 1, ["UNM"] = 1, ["LEN"] = 2,
		["ADDVN"] = 1, ["SUBVN"] = 1, ["MULVN"] = 1, ["DIVVN"] = 1, ["MODVN"] = 3, ["ADDNV"] = 1, ["SUBNV"] = 1, ["MULNV"] = 1, ["DIVNV"] = 1, ["MODNV"] = 3,
		["ADDVV"] = 1.5, ["SUBVV"] = 1.5, ["MULVV"] = 1.5, ["DIVVV"] = 1.5, ["MODVV"] = 4.5, ["POW"] = 2, ["CAT"] = 5,
		["KSTR"] = 1, ["KCDATA"] = 1, ["KSHORT"] = 1, ["KNUM"] = 1, ["KPRI"] = 1, ["KNIL"] = 1,
		["UGET"] = 3, ["USETV"] = 3.5, ["USETS"] = 3, ["USETN"] = 3, ["USETP"] = 3, ["UCLO"] = 0, ["FNEW"] = 1, ["TNEW"] = 8, ["TDUP"] = 6, ["GGET"] = 4, ["GSET"] = 5,
		["TGETV"] = 2.5, ["TGETS"] = 2, ["TGETB"] = 1.5, ["TGETR"] = 1.5, ["TSETV"] = 3.5, ["TSETS"] = 3, ["TSETB"] = 2.5, ["TSETM"] = 2.5, ["TSETR"] = 2.5,
		["CALLM"] = 5, ["CALL"] = 5, ["CALLMT"] = 5, ["CALLT"] = 5, ["ITERC"] = 2, ["ITERN"] = 2, ["VARG"] = 0, ["ISNEXT"] = 5,
		["RETM"] = 1, ["RET"] = 1, ["RET0"] = 0, ["RET1"] = 0, ["FORI"] = 1, ["FORL"] = 0, ["ITERL"] = 0, ["LOOP"] = 1, ["JMP"] = 0, ["CLOSURE"] = 10,
	};

	public sealed class LineHeat
	{
		public double Weight { get; set; }

		public Dictionary<string, (int Count, double Weight)> Opcodes { get; } = [with(StringComparer.Ordinal)];
	}

	private sealed class Proto
	{
		public int FirstLine { get; set; }

		public int LineCount { get; set; }

		public List<(int Op, int D, int Line)> Instructions { get; } = [];

		public List<Proto?> Constants { get; } = [];

		public List<Proto> Children { get; } = [];
	}

	private sealed class Chunk(byte[] buffer)
	{
		public byte[] Buffer { get; } = buffer;

		public int Caret { get; set; } = 4;

		public int Version { get; set; }

		public int Flags { get; set; }

		public List<Proto> Protos { get; } = [];

		public Proto? Main { get; set; }

		public bool Stripped => (Flags & 0x02) != 0;
	}

	private static int Uleb(byte[] buffer, ref int caret) {
		int value = buffer[caret++];

		if (value < 0x80) return value;

		value -= 0x80;
		int shift = 7;
		int read;

		do {
			read = buffer[caret++];
			value += (read & 0x7f) << shift;
			shift += 7;
		}
		while (read >= 0x80);

		return value;
	}

	private static void SkipTableValue(byte[] buffer, ref int caret) {
		int type = Uleb(buffer, ref caret);

		if (type == 3) Uleb(buffer, ref caret);
		else if (type == 4) {
			Uleb(buffer, ref caret);
			Uleb(buffer, ref caret);
		}
		else if (type > 5) caret += type - 5;
	}

	private static Proto? ReadConstant(Chunk chunk, Proto proto, ref int caret) {
		int type = Uleb(chunk.Buffer, ref caret);

		if (type == 0) {
			Proto child = chunk.Protos[^1];
			chunk.Protos.RemoveAt(chunk.Protos.Count - 1);
			proto.Children.Add(child);
			return child;
		}

		if (type == 1) {
			int array = Uleb(chunk.Buffer, ref caret);
			int hash = Uleb(chunk.Buffer, ref caret);

			for (int i = 0; i < array; i++) SkipTableValue(chunk.Buffer, ref caret);

			for (int i = 0; i < hash; i++) {
				SkipTableValue(chunk.Buffer, ref caret);
				SkipTableValue(chunk.Buffer, ref caret);
			}
		}
		else if (type > 5) caret += type - 5;

		return null;
	}

	private static int DebugLine(byte[] buffer, ref int caret, int bytes, bool little) {
		int at = caret;

		switch (bytes) {
			case 1:
				caret += 1;
				return buffer[at];
			case 2:
				caret += 2;
				return little ? buffer[at] + (buffer[at + 1] << 8) : buffer[at + 1] + (buffer[at] << 8);
			default:
				caret += 4;
				return little ? buffer[at] + (buffer[at + 1] << 8) + (buffer[at + 2] << 16) + (buffer[at + 3] << 24) : buffer[at + 3] + (buffer[at + 2] << 8) + (buffer[at + 1] << 16) + (buffer[at] << 24);
		}
	}

	private static Proto ReadProto(Chunk chunk, int length) {
		byte[] buffer = chunk.Buffer;
		int caret = chunk.Caret + 3;
		Proto proto = new();
		int upvalues = buffer[caret++];
		int constants = Uleb(buffer, ref caret);
		Uleb(buffer, ref caret);
		int instructions = Uleb(buffer, ref caret);
		int debugSize = 0;

		if (!chunk.Stripped) {
			debugSize = Uleb(buffer, ref caret);

			if (debugSize != 0) {
				proto.FirstLine = Uleb(buffer, ref caret);
				proto.LineCount = Uleb(buffer, ref caret);
			}
		}

		for (int i = 0; i < instructions; i++) {
			proto.Instructions.Add((buffer[caret], buffer[caret + 2] + (buffer[caret + 3] << 8), 0));
			caret += 4;
		}

		caret += upvalues * 2;

		for (int i = 0; i < constants; i++) proto.Constants.Add(ReadConstant(chunk, proto, ref caret));

		if (debugSize != 0) {
			caret = chunk.Caret + length - debugSize;
			int bytes = proto.LineCount < 256 ? 1 : proto.LineCount < 65536 ? 2 : 4;
			bool little = (chunk.Flags & 0x01) == 0;

			for (int i = 0; i < instructions; i++) proto.Instructions[i] = proto.Instructions[i] with { Line = DebugLine(buffer, ref caret, bytes, little) };
		}

		return proto;
	}

	private static Chunk ReadChunk(byte[] bytecode) {
		if (bytecode.Length < 4 || bytecode[0] != 27 || bytecode[1] != 76 || bytecode[2] != 74) throw new InvalidDataException("Bytecode signature mismatch. Is this LuaJIT bytecode?");

		int version = bytecode[3];

		if (version is 0 or > 2) throw new InvalidDataException("Unsupported bytecode version, only 1 and 2 are supported. Got " + version);

		Chunk chunk = new(bytecode) { Version = version };
		int caret = chunk.Caret;
		chunk.Flags = Uleb(bytecode, ref caret);

		if ((chunk.Flags & ~0x0F) != 0) throw new InvalidDataException("Unknown flag (" + chunk.Flags.ToString("x", CultureInfo.InvariantCulture) + ")");

		if (!chunk.Stripped) {
			int nameLength = Uleb(bytecode, ref caret);
			caret += nameLength;
		}

		chunk.Caret = caret;

		while (true) {
			caret = chunk.Caret;
			int length = Uleb(bytecode, ref caret);
			chunk.Caret = caret;

			if (length == 0) break;

			chunk.Protos.Add(ReadProto(chunk, length));
			chunk.Caret += length;
		}

		chunk.Main = chunk.Protos.Count > 0 ? chunk.Protos[^1] : null;
		return chunk;
	}

	private static string OpcodeName(int version, int op) {
		string names = version == 1 ? Names20 : Names21;
		return op * 6 + 6 <= names.Length ? names.Substring(op * 6, 6).Trim() : string.Empty;
	}

	private static double Map(Proto proto, Chunk chunk, Dictionary<int, LineHeat> heat, double max) {
		foreach ((int op, int d, int lineOffset) in proto.Instructions) {
			int line = proto.FirstLine + lineOffset;
			string opcode = OpcodeName(chunk.Version, op);

			if (opcode == "FNEW") {
				int constant = proto.Constants.Count - 1 - d;

				if ((uint)constant < (uint)proto.Constants.Count && proto.Constants[constant] is { } child) line = child.FirstLine;
			}

			if (!heat.TryGetValue(line, out LineHeat? entry)) heat[line] = entry = new LineHeat();

			if (!Weights.TryGetValue(opcode, out double weight)) continue;

			if (opcode == "FNEW" && chunk.Main != proto) opcode = "CLOSURE";

			(int count, double total) = entry.Opcodes.GetValueOrDefault(opcode);
			entry.Opcodes[opcode] = (count + 1, total + weight);
			entry.Weight += weight;
			max = Math.Max(max, entry.Weight);
		}

		foreach (Proto child in proto.Children) max = Map(child, chunk, heat, max);

		return max;
	}

	public static (Dictionary<int, LineHeat> Heat, double Max) Compute(byte[] bytecode) {
		Chunk chunk = ReadChunk(bytecode);
		Dictionary<int, LineHeat> heat = [];

		if (chunk.Stripped || chunk.Main is null) return (heat, 0);

		return (heat, Map(chunk.Main, chunk, heat, 0));
	}

	private static double Round2(double value) => Math.Round((value + double.Epsilon) * 100) / 100;

	private static double EaseIn(double x) => 1 - Math.Sqrt(1 - x);

	public static void Generate(GLuaExtension extension, ICommandHost host) {
		if (host.GetActiveFilePath() is not { } path) {
			host.ShowMessage(MessageSeverity.Error, "No active text editor!", []);
			return;
		}

		if (!path.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)) {
			host.ShowMessage(MessageSeverity.Error, "This isn't a valid GLua file", []);
			return;
		}

		string text = host.GetDocumentText(path) ?? File.ReadAllText(path);

		_ = Task.Run(() => {
			byte[] output;

			try {
				output = LuaShared.Compile(text, path);
			}
			catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) {
				host.ShowMessage(MessageSeverity.Error, "Error: " + exception.Message, []);
				return;
			}

			Dictionary<int, LineHeat> heat;
			double max;

			try {
				(heat, max) = Compute(output);
			}
			catch (Exception exception) when (exception is InvalidDataException or IndexOutOfRangeException or ArgumentOutOfRangeException) {
				host.ShowMessage(MessageSeverity.Error, "Bytecode error: " + exception.Message, []);
				return;
			}

			Dictionary<int, Color> backgrounds = [];
			Dictionary<int, string> hovers = [];

			foreach ((int line, LineHeat entry) in heat) {
				if (entry.Weight == 0 || line < 1) continue;

				StringBuilder table = new("| Opcode | Count | Weight/op | Weight | % |\n|:-:|:-:|:-:|:-:|:-:|\n");

				foreach ((string opcode, (int count, double total)) in entry.Opcodes.OrderByDescending(static pair => pair.Value.Weight)) {
					table.Append("| [`").Append(opcode).Append("`](").Append(opcode == "CLOSURE" ? ClosureUrl : LuaJitBytecodeUrl).Append(") | ").Append(count).Append(" | ")
						.Append(Round2(Weights[opcode]).ToString(CultureInfo.InvariantCulture)).Append(" | ").Append(Round2(total).ToString(CultureInfo.InvariantCulture)).Append(" | ")
						.Append(Round2(total / entry.Weight * 100).ToString(CultureInfo.InvariantCulture)).Append("% |\n");
				}

				double ratio = max > 0 ? entry.Weight / max : 0;
				backgrounds[line - 1] = new Color(1f, 0f, 0f, (float)(EaseIn(ratio) / 2));
				hovers[line - 1] = "Bytecode Weight: " + Round2(entry.Weight).ToString(CultureInfo.InvariantCulture) + " (File Max: " + Round2(max).ToString(CultureInfo.InvariantCulture) + ")\n\nHeat: " + Round2(ratio * 100).ToString(CultureInfo.InvariantCulture) + "%\n\n" + table;
			}

			extension.SetHeatmap(path, backgrounds, hovers);
			host.Refresh();
		});
	}
}
