namespace Forge.Common.Commands;

public interface IOutputChannel
{
	string GetName();
	int GetCount();

	void OnAppended(Action<IOutputChannel> handler);

	void Append(string line);
	void Clear();

	int CopyTo(string[] destination, int skip);
	string ReadAll();
	string ReadRange(int first, int last);
}

public interface IOutputChannels
{
	IReadOnlyList<IOutputChannel> All();

	void OnAdded(Action handler);

	IOutputChannel Get(string name);
}
