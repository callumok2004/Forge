using Microsoft.Extensions.DependencyInjection;
using System.Reflection;

namespace Forge.Common;

public sealed class ForgeBuilder
{
	private const BindingFlags StaticMembers = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

	private readonly ServiceCollection _services = [];
	private readonly List<Assembly> _assemblies = [];
	private readonly List<Type> _components = [];
	private readonly Dictionary<Type, object> _published = [];

	public ForgeBuilder() => _services.AddSingleton(this);

	public IServiceProvider? Services { get; private set; }

	public ForgeBuilder WithAssembly(string name) {
		try {
			_assemblies.Add(Assembly.Load(name));
		}
		catch (Exception exception) when (exception is FileNotFoundException or BadImageFormatException) {
		}

		return this;
	}

	public ForgeBuilder WithAssemblyOf<T>() => Then(() => _assemblies.Add(typeof(T).Assembly));

	public ForgeBuilder WithComponent<TService, TImplementation>()
		where TService : class
		where TImplementation : class, TService
		=> Then(() => _services.AddSingleton<TService, TImplementation>());

	public ForgeBuilder WithComponent<TImplementation>()
		where TImplementation : class
		=> Then(() => _services.AddSingleton<TImplementation>());

	public ForgeBuilder WithInstance<TService>(TService instance)
		where TService : class
		=> Then(() => _services.AddSingleton(instance));

	public ForgeBuilder WithInstance<TService, TImplementation>(TImplementation instance)
		where TService : class
		where TImplementation : class, TService
		=> Then(() => _services.AddSingleton(instance).AddSingleton<TService>(instance));

	public ForgeBuilder WithResolvedComponent<TService, TImplementation>(Func<IServiceProvider, TImplementation> resolve)
		where TService : class
		where TImplementation : class, TService
		=> Then(() => _services.AddSingleton<TService>(resolve));

	public IServiceProvider Build() {
		foreach (Assembly assembly in Distinct(_assemblies)) {
			foreach (Type type in assembly.GetTypes()) {
				if (type.GetCustomAttribute<ForgeComponentAttribute>() is null) continue;

				_components.Add(type);
				type.GetMethod("Link", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, [_services]);
			}
		}

		Services = _services.BuildServiceProvider(new ServiceProviderOptions {
			ValidateOnBuild = false,
			ValidateScopes = false,
		});

		Inject();

		return Services;
	}

	public ForgeBuilder Publish<TService>(TService instance)
		where TService : class
		=> Then(() => _published[typeof(TService)] = instance);

	public void Inject() {
		foreach (Type component in _components) Inject(component);
	}

	private object? Resolve(Type type) =>
		_published.TryGetValue(type, out object? published) ? published : Services?.GetService(type);

	private ForgeBuilder Then(Action action) {
		action();
		return this;
	}

	private static IEnumerable<Assembly> Distinct(List<Assembly> assemblies) {
		HashSet<Assembly> seen = [];

		foreach (Assembly assembly in assemblies) {
			if (seen.Add(assembly)) yield return assembly;
		}
	}

	private void Inject(Type component) {
		foreach (FieldInfo field in component.GetFields(StaticMembers)) {
			if (field.GetCustomAttribute<DependencyAttribute>() is null) continue;

			if (Resolve(field.FieldType) is { } value) field.SetValue(null, value);
		}

		foreach (PropertyInfo property in component.GetProperties(StaticMembers)) {
			if (property.GetCustomAttribute<DependencyAttribute>() is null) continue;

			if (Resolve(property.PropertyType) is { } value) property.GetSetMethod(nonPublic: true)?.Invoke(null, [value]);
		}
	}
}
