namespace Forge.Extensions.Dotnet.Layout;

public enum TypeKind
{
	Class,
	Struct,
	RecordClass,
	RecordStruct,
	Enum,
	Interface,
}

public readonly record struct TextPart(string Text, int Offset);

public sealed class FieldDeclaration
{
	public required string Name { get; init; }

	public required string TypeText { get; init; }

	public int TypeOffset { get; init; }

	public int NameOffset { get; init; }

	public string? FixedCount { get; init; }

	public int? FieldOffset { get; init; }

	public bool IsEvent { get; init; }

	public string? Initializer { get; set; }
}

public sealed class TypeDeclaration
{
	public required TypeKind Kind { get; init; }

	public required string Name { get; init; }

	public required List<string> TypeParameters { get; init; }

	public required string Attributes { get; init; }

	public required List<string> Modifiers { get; init; }

	public required List<TextPart> BaseList { get; init; }

	public TextPart? EnumBase { get; init; }

	public required List<FieldDeclaration> Fields { get; init; }

	public int NameOffset { get; init; }

	public int BodyStart { get; init; } = -1;

	public int BodyEnd { get; init; } = -1;

	public bool IsClass => Kind is TypeKind.Class or TypeKind.RecordClass;

	public bool IsStruct => Kind is TypeKind.Struct or TypeKind.RecordStruct;
}
