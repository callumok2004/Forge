using System.Collections.Concurrent;

namespace Forge.Language;

public sealed class UiQueue(Action refresh)
{
	private readonly ConcurrentQueue<Action> _work = new();

	public void Post(Action action) {
		_work.Enqueue(action);
		refresh();
	}

	public void Drain() {
		while (_work.TryDequeue(out Action? action)) action();
	}
}
