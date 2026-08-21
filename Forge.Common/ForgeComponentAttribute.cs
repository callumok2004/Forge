namespace Forge.Common;

[AttributeUsage(AttributeTargets.Class)]
public sealed class ForgeComponentAttribute : Attribute
{
}

[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
public sealed class DependencyAttribute : Attribute
{
}
