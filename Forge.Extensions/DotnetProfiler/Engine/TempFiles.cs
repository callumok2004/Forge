namespace Forge.Extensions.DotnetProfiler.Engine;

public static class TempFiles
{
	private static readonly string[] Patterns = ["profiler-*.dmp", "profiler-*.nettrace"];

	public static string Directory => Path.GetTempPath();

	public static string NewDumpPath(int pid) => Path.Combine(Directory, $"profiler-heap-{pid}-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.dmp");

	private static IEnumerable<FileInfo> Files() {
		foreach (string pattern in Patterns) {
			IEnumerable<string> matches;

			try {
				matches = System.IO.Directory.EnumerateFiles(Directory, pattern);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
				continue;
			}

			foreach (string path in matches) yield return new FileInfo(path);
		}
	}

	public static TempSurvey Survey() {
		int count = 0;
		long bytes = 0;

		foreach (FileInfo file in Files()) {
			try {
				bytes += file.Length;
				count++;
			}
			catch (IOException) {
			}
		}

		return new TempSurvey(count, bytes, Directory);
	}

	public static TempCleanup Cleanup(TimeSpan olderThan) {
		DateTime cutoff = DateTime.UtcNow - olderThan;
		int deleted = 0;
		int locked = 0;
		long bytes = 0;

		foreach (FileInfo file in Files()) {
			try {
				if (file.LastWriteTimeUtc > cutoff) continue;

				long size = file.Length;
				file.Delete();
				bytes += size;
				deleted++;
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
				locked++;
			}
		}

		return new TempCleanup(deleted, bytes, locked, Directory);
	}
}
