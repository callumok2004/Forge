using System.IO.Pipes;
using System.Text;

namespace Forge.Core.Native;

public static class UriLauncher
{
	public const string Scheme = "forge-dev";

	private const string PipeName = "ForgeDev.Forge.Uri";

	public static bool IsUri(ReadOnlySpan<char> argument) => argument.Length > Scheme.Length + 1 && argument.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase) && argument[Scheme.Length] == ':';

	public static string? Find(string[] args) {
		foreach (string argument in args) {
			if (IsUri(argument)) return argument;
		}

		return null;
	}

	public static bool TryForward(string uri) {
		try {
			using NamedPipeClientStream client = new(".", PipeName, PipeDirection.Out);
			client.Connect(200);
			client.Write(Encoding.UTF8.GetBytes(uri));
			return true;
		}
		catch (TimeoutException) {
			return false;
		}
		catch (IOException) {
			return false;
		}
	}

	public static void Listen(Action<string> received) {
		Thread thread = new(() => Serve(received)) { IsBackground = true, Name = "Forge URI listener" };
		thread.Start();
	}

	private static void Serve(Action<string> received) {
		byte[] buffer = new byte[8192];

		int failures = 0;

		while (true) {
			NamedPipeServerStream server;

			try {
				server = new(PipeName, PipeDirection.In, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly);
				failures = 0;
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
				if (++failures >= 5) return;

				Thread.Sleep(1000 * failures);
				continue;
			}

			using (server) try {
				server.WaitForConnection();

				int length = 0;
				int read;

				while (length < buffer.Length && (read = server.Read(buffer, length, buffer.Length - length)) > 0) length += read;

				string uri = Encoding.UTF8.GetString(buffer, 0, length);

				if (IsUri(uri)) received(uri);
			}
			catch (IOException) {
			}
		}
	}
}
