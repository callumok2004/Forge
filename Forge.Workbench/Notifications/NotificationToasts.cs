using Forge.Common.Commands;
using Forge.Common.UI;
using Forge.Theming;

namespace Forge.Workbench;

public sealed class NotificationToasts : Panel
{
	private const int MaxVisible = 3;
	private const int TimeoutMilliseconds = 10_000;

	private readonly List<(NotificationToast Toast, long Expires)> _toasts = [];

	public NotificationToasts() {
		Name = "Notifications";
		MouseInputEnabled = false;
	}

	public void Show(MessageSeverity severity, string message, IReadOnlyList<string> actions, Action<int>? chosen) {
		NotificationToast toast = new(severity, message, actions, chosen) { Dismissed = Remove };

		_toasts.Add((toast, toast.Sticky ? long.MaxValue : Environment.TickCount64 + TimeoutMilliseconds));
		AddChild(toast);

		while (_toasts.Count > MaxVisible) Remove(_toasts[0].Toast);

		if (!toast.Sticky) AddTickSignal(1000);

		InvalidateLayout();
		Repaint();
	}

	private void Remove(NotificationToast toast) {
		int index = _toasts.FindIndex(entry => entry.Toast == toast);

		if (index < 0) return;

		_toasts.RemoveAt(index);
		RemoveChild(toast);
		toast.Resolve(-1);
		InvalidateLayout();
		Repaint();
	}

	public override void OnTick() {
		long now = Environment.TickCount64;
		bool waiting = false;

		for (int i = _toasts.Count - 1; i >= 0; i--) {
			(NotificationToast toast, long expires) = _toasts[i];

			if (expires == long.MaxValue) continue;

			if (toast.IsCursorOver) {
				_toasts[i] = (toast, now + TimeoutMilliseconds);
				waiting = true;
			}
			else if (now >= expires) Remove(toast);
			else waiting = true;
		}

		if (!waiting) RemoveTickSignal();
	}

	public float ContentHeight {
		get {
			float gap = Metrics.Space8 * Scale;
			float height = 0f;

			foreach ((NotificationToast toast, _) in _toasts) height += toast.PreferredHeight + gap;

			return height;
		}
	}

	public override void PerformLayout() {
		float gap = Metrics.Space8 * Scale;
		float bottom = Tall;

		for (int i = _toasts.Count - 1; i >= 0; i--) {
			NotificationToast toast = _toasts[i].Toast;
			float height = toast.PreferredHeight;

			bottom -= height;
			toast.SetBounds(0f, bottom, Wide, height);
			bottom -= gap;
		}
	}
}
