namespace Forge.UI.Layout;

public sealed class SplitLayout
{
	public sealed class Item(float size, Func<float> minimum, Func<float> maximum)
	{
		public float Size { get; set; } = size;

		public float MinimumSize => minimum();

		public float MaximumSize => maximum();
	}

	private readonly List<Item> _items = [];
	private float?[]? _proportions;
	private float _contentSize;
	private float[]? _dragSizes;
	private int _dragIndex = -1;
	private float _dragStart;

	public float Size { get; private set; }

	public IReadOnlyList<Item> Items => _items;

	public float ContentSize => _contentSize;

	public bool Dragging => _dragIndex >= 0;

	public Item Add(float size, Func<float> minimum, Func<float> maximum, int index) {
		Item item = new(size, minimum, maximum);
		_items.Insert(Math.Clamp(index, 0, _items.Count), item);
		Relayout([_items.IndexOf(item)]);
		return item;
	}

	public void Remove(int index, bool distribute) {
		if (index < 0 || index >= _items.Count) return;

		_items.RemoveAt(index);
		Relayout(null);

		if (distribute) DistributeViewSizes();
	}

	public void Layout(float size) {
		float previous = MathF.Max(Size, _contentSize);
		Size = size;

		if (_proportions is null) Resize(_items.Count - 1, size - previous, null, null);
		else {
			float total = 0f;
			float remaining = size;

			for (int i = 0; i < _items.Count; i++) {
				if (Proportion(i) is { } proportion) total += proportion;
				else remaining -= _items[i].Size;
			}

			for (int i = 0; i < _items.Count; i++) {
				if (Proportion(i) is { } proportion && total > 0f) _items[i].Size = Clamp(MathF.Round(proportion * remaining / total), _items[i].MinimumSize, _items[i].MaximumSize);
			}
		}

		DistributeEmptySpace(null);
		UpdateContentSize();
	}

	private float? Proportion(int index) => _proportions is { } proportions && index < proportions.Length ? proportions[index] : null;

	public void ViewChanged(Item item, float? size) {
		int index = _items.IndexOf(item);

		if (index < 0) return;

		item.Size = Clamp(size ?? item.Size, item.MinimumSize, item.MaximumSize);
		Relayout([index]);
	}

	public bool CanResize(int sash) {
		if (sash < 0 || sash >= _items.Count - 1) return false;

		float minDeltaUp = 0f;
		float maxDeltaUp = 0f;
		float minDeltaDown = 0f;
		float maxDeltaDown = 0f;

		for (int i = sash; i >= 0; i--) {
			minDeltaUp += _items[i].MinimumSize - _items[i].Size;
			maxDeltaUp += _items[i].MaximumSize - _items[i].Size;
		}

		for (int i = sash + 1; i < _items.Count; i++) {
			maxDeltaDown += _items[i].Size - _items[i].MinimumSize;
			minDeltaDown += _items[i].Size - _items[i].MaximumSize;
		}

		float minDelta = MathF.Max(minDeltaUp, minDeltaDown);
		float maxDelta = MathF.Min(maxDeltaDown, maxDeltaUp);

		return minDelta < 0f || maxDelta > 0f;
	}

	public void SashStart(int sash, float position) {
		_dragIndex = sash;
		_dragStart = position;
		_dragSizes = [.. _items.Select(static item => item.Size)];
	}

	public void SashChange(float position) {
		if (_dragIndex < 0 || _dragSizes is null) return;

		Resize(_dragIndex, position - _dragStart, _dragSizes, null);
		DistributeEmptySpace(null);
		UpdateContentSize();
	}

	public void SashEnd() {
		_dragIndex = -1;
		_dragSizes = null;
		SaveProportions();
	}

	public void DistributeViewSizes() {
		List<Item> flexible = [];
		float flexibleSize = 0f;

		foreach (Item item in _items) {
			if (item.MaximumSize - item.MinimumSize <= 0f) continue;

			flexible.Add(item);
			flexibleSize += item.Size;
		}

		if (flexible.Count == 0) return;

		float size = MathF.Floor(flexibleSize / flexible.Count);

		foreach (Item item in flexible) item.Size = Clamp(size, item.MinimumSize, item.MaximumSize);

		Relayout(null);
	}

	private void Relayout(int[]? lowPriority) {
		float content = 0f;

		foreach (Item item in _items) content += item.Size;

		Resize(_items.Count - 1, Size - content, null, lowPriority);
		DistributeEmptySpace(null);
		UpdateContentSize();
		SaveProportions();
	}

	private void UpdateContentSize() {
		float content = 0f;

		foreach (Item item in _items) content += item.Size;

		_contentSize = content;
	}

	private void SaveProportions() {
		if (_contentSize <= 0f) return;

		_proportions = [.. _items.Select(item => (float?)(item.Size / _contentSize))];
	}

	private float Resize(int index, float delta, float[]? sizes, int[]? lowPriority) {
		if (index < 0 || index >= _items.Count) return 0f;

		sizes ??= [.. _items.Select(static item => item.Size)];

		List<int> up = [];
		List<int> down = [];

		for (int i = index; i >= 0; i--) up.Add(i);

		for (int i = index + 1; i < _items.Count; i++) down.Add(i);

		if (lowPriority is not null) {
			foreach (int low in lowPriority) {
				PushToEnd(up, low);
				PushToEnd(down, low);
			}
		}

		float minDeltaUp = 0f;
		float maxDeltaUp = 0f;

		foreach (int i in up) {
			minDeltaUp += _items[i].MinimumSize - sizes[i];
			maxDeltaUp += _items[i].MaximumSize - sizes[i];
		}

		float maxDeltaDown = down.Count == 0 ? float.PositiveInfinity : 0f;
		float minDeltaDown = down.Count == 0 ? float.NegativeInfinity : 0f;

		foreach (int i in down) {
			maxDeltaDown += sizes[i] - _items[i].MinimumSize;
			minDeltaDown += sizes[i] - _items[i].MaximumSize;
		}

		float minDelta = MathF.Max(minDeltaUp, minDeltaDown);
		float maxDelta = MathF.Min(maxDeltaDown, maxDeltaUp);

		delta = Clamp(delta, minDelta, maxDelta);

		float deltaUp = delta;

		foreach (int i in up) {
			Item item = _items[i];
			float size = Clamp(sizes[i] + deltaUp, item.MinimumSize, item.MaximumSize);
			deltaUp -= size - sizes[i];
			item.Size = size;
		}

		float deltaDown = delta;

		foreach (int i in down) {
			Item item = _items[i];
			float size = Clamp(sizes[i] - deltaDown, item.MinimumSize, item.MaximumSize);
			deltaDown += size - sizes[i];
			item.Size = size;
		}

		return delta;
	}

	private void DistributeEmptySpace(int? lowPriority) {
		float content = 0f;

		foreach (Item item in _items) content += item.Size;

		float empty = Size - content;
		List<int> indexes = [];

		for (int i = _items.Count - 1; i >= 0; i--) indexes.Add(i);

		if (lowPriority is { } low) PushToEnd(indexes, low);

		for (int i = 0; empty != 0f && i < indexes.Count; i++) {
			Item item = _items[indexes[i]];
			float size = Clamp(item.Size + empty, item.MinimumSize, item.MaximumSize);
			empty -= size - item.Size;
			item.Size = size;
		}
	}

	private static float Clamp(float value, float minimum, float maximum) => MathF.Min(MathF.Max(value, minimum), maximum);

	private static void PushToEnd(List<int> list, int value) {
		if (list.Remove(value)) list.Add(value);
	}
}
