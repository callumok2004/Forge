namespace Forge.Core;

public sealed class DamageRegion
{
	public const int Capacity = 4;

	private readonly PixelRect[] _rects = new PixelRect[Capacity + 1];
	private int _count;

	public int Count => _count;

	public bool IsEmpty => _count == 0;

	public PixelRect this[int index] => _rects[index];

	public void Clear() => _count = 0;

	public PixelRect Bounds() {
		PixelRect bounds = PixelRect.Empty;

		for (int i = 0; i < _count; i++) bounds = bounds.Union(_rects[i]);

		return bounds;
	}

	public long Area() {
		long area = 0;

		for (int i = 0; i < _count; i++) area += (long)_rects[i].Width * _rects[i].Height;

		return area;
	}

	public void Add(DamageRegion other) {
		for (int i = 0; i < other._count; i++) Add(other._rects[i]);
	}

	public void Add(PixelRect rect) {
		if (rect.IsEmpty) return;

		for (int i = 0; i < _count; i++) {
			if (_rects[i].Contains(rect)) return;
		}

		int kept = 0;
		for (int i = 0; i < _count; i++) {
			if (!rect.Contains(_rects[i])) _rects[kept++] = _rects[i];
		}

		_count = kept;

		if (_count < Capacity) {
			_rects[_count++] = rect;
			return;
		}

		_rects[_count++] = rect;
		MergeCheapestPair();
	}

	private void MergeCheapestPair() {
		int bestA = 0;
		int bestB = 1;
		long bestWaste = long.MaxValue;

		for (int a = 0; a < _count; a++) {
			for (int b = a + 1; b < _count; b++) {
				PixelRect union = _rects[a].Union(_rects[b]);

				long waste = ((long)union.Width * union.Height)
					- ((long)_rects[a].Width * _rects[a].Height)
					- ((long)_rects[b].Width * _rects[b].Height);

				if (waste < bestWaste) {
					bestWaste = waste;
					bestA = a;
					bestB = b;
				}
			}
		}

		_rects[bestA] = _rects[bestA].Union(_rects[bestB]);
		_rects[bestB] = _rects[_count - 1];
		_count--;
	}
}
