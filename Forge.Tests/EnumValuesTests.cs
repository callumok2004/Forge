using System.Numerics;

using Forge.Extensions.Dotnet.Language;

namespace Forge.Tests;

public class EnumValuesTests
{
	[Fact]
	public void EvaluatesImplicitAndExpressionMembers() {
		string text = "public enum A : int\n{\n\tNone,\n\tOne = 1 << 3, // shift\n\tTwo,\n\t[Obsolete] Three = One | 0x2,\n#if X\n\tFour = 'a',\n#endif\n\tFive = (int)Two + 1\n}\n";

		List<EnumValues.Member> members = EnumValues.Parse(text);

		Assert.Equal(["None", "One", "Two", "Three", "Four", "Five"], members.Select(static m => m.Name));
		Assert.Equal([0, 8, 9, 10, 97, 10], members.Select(static m => (int)(m.Value ?? -1)));
		Assert.Equal([2, 3, 4, 5, 7, 9], members.Select(static m => m.Line));
		Assert.True(members.All(static m => !m.Literal));
	}

	[Fact]
	public void UnknownReferencesPoisonFollowingMembers() {
		List<EnumValues.Member> members = EnumValues.Parse("enum B { X = Other.Value, Y }");

		Assert.Null(members[0].Value);
		Assert.Null(members[1].Value);
	}

	[Fact]
	public void AppendsValueToHover() {
		string? hover = EnumValues.AppendToHover("```csharp\nA.One = 1 << 3\n```", ["enum A { One = 1 << 3 }"]);

		Assert.Equal("```csharp\nA.One = 1 << 3\n```\n\nValue: `8`", hover);
		Assert.Equal(new BigInteger(8), EnumValues.Parse("enum A { One = 1 << 3 }")[0].Value);
	}
}
