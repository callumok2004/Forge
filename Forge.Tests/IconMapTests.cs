using Forge.Render;

namespace Forge.Tests;

public class IconMapTests
{
	private static string MapPath =>
		Path.Combine(AppContext.BaseDirectory, "Assets", "material-icons.map");

	private static IconMap Load() => IconMap.Load(MapPath);

	private static (Dictionary<string, string> Ext, Dictionary<string, string> Name,
		Dictionary<string, string> Dir, Dictionary<string, string> DirOpen) ReadNaively() {
		Dictionary<string, string> ext = [with(StringComparer.OrdinalIgnoreCase)];
		Dictionary<string, string> name = [with(StringComparer.OrdinalIgnoreCase)];
		Dictionary<string, string> dir = [with(StringComparer.OrdinalIgnoreCase)];
		Dictionary<string, string> dirOpen = [with(StringComparer.OrdinalIgnoreCase)];

		foreach (string line in File.ReadLines(MapPath)) {
			int first = line.IndexOf('\t');
			int second = first < 0 ? -1 : line.IndexOf('\t', first + 1);

			if (second < 0) continue;

			string key = line[(first + 1)..second];
			string icon = line[(second + 1)..];

			switch (line.AsSpan(0, first)) {
				case "ext": ext[key] = icon; break;
				case "name": name[key] = icon; break;
				case "dir": dir[key] = icon; break;
				case "diropen": dirOpen[key] = icon; break;
			}
		}

		return (ext, name, dir, dirOpen);
	}

	[Fact]
	public void TheAssetIsWhereTheBuildPutsIt() {
		Assert.True(File.Exists(MapPath), $"{MapPath} is missing; the rest of these prove nothing.");
	}

	[Fact]
	public void EveryFolderKeyResolvesToWhatTheDictionaryWouldHaveSaid() {
		IconMap map = Load();
		(_, _, Dictionary<string, string> dir, Dictionary<string, string> dirOpen) = ReadNaively();

		Assert.NotEmpty(dir);

		foreach ((string key, string expected) in dir) {
			int line = map.FindFolder(key, isExpanded: false);

			Assert.True(line >= 0, $"folder '{key}' was not found");
			Assert.Equal(expected, map.IconName(line));
		}

		foreach ((string key, string expected) in dirOpen) {
			int line = map.FindFolder(key, isExpanded: true);

			Assert.True(line >= 0, $"expanded folder '{key}' was not found");
			Assert.Equal(expected, map.IconName(line));
		}
	}

	[Fact]
	public void EveryFullFileNameResolvesToWhatTheDictionaryWouldHaveSaid() {
		IconMap map = Load();
		(_, Dictionary<string, string> names, _, _) = ReadNaively();

		Assert.NotEmpty(names);

		foreach ((string key, string expected) in names) {
			int line = map.FindFile(key);

			Assert.True(line >= 0, $"file name '{key}' was not found");
			Assert.Equal(expected, map.IconName(line));
		}
	}

	[Fact]
	public void ExtensionsResolveThroughTheSameWalkTheShellUses() {
		IconMap map = Load();
		(Dictionary<string, string> ext, Dictionary<string, string> names, _, _) = ReadNaively();

		Assert.NotEmpty(ext);

		foreach ((string key, string expected) in ext) {
			string fileName = "zzprobe." + key;

			if (names.ContainsKey(fileName)) continue;

			int line = map.FindFile(fileName);

			Assert.True(line >= 0, $"extension '{key}' was not found");

			if (!key.Contains('.')) Assert.Equal(expected, map.IconName(line));
		}
	}

	[Fact]
	public void CaseIsIgnoredTheWayTheDictionaryIgnoredIt() {
		IconMap map = Load();
		(Dictionary<string, string> ext, _, _, _) = ReadNaively();

		string key = ext.Keys.First(candidate =>
			!candidate.Contains('.') && candidate.Any(char.IsLetter));

		int lower = map.FindFile("zzprobe." + key.ToLowerInvariant());
		int upper = map.FindFile("zzprobe." + key.ToUpperInvariant());

		Assert.True(lower >= 0);
		Assert.Equal(map.IconName(lower), map.IconName(upper));
	}

	[Fact]
	public void SomethingThatIsNotInTheTableIsNotFound() {
		IconMap map = Load();

		Assert.Equal(-1, map.FindFile("zzprobe.definitely-not-a-real-extension"));
		Assert.Equal(-1, map.FindFolder("zz-definitely-not-a-real-folder", isExpanded: false));
		Assert.Equal(-1, map.FindFolder("zz-definitely-not-a-real-folder", isExpanded: true));
	}

	[Fact]
	public void AnAbsurdlyLongKeyIsRejectedRatherThanOverflowing() {
		IconMap map = Load();

		Assert.Equal(-1, map.FindFolder(new string('a', 4096), isExpanded: false));
		Assert.Equal(-1, map.FindFile("x." + new string('a', 4096)));
	}

	[Fact]
	public void TheDefaultsAreRead() {
		IconMap map = Load();

		Assert.NotEmpty(map.DefaultFile);
		Assert.NotEmpty(map.DefaultFolder);
		Assert.NotEmpty(map.DefaultFolderExpanded);
	}

	[Fact]
	public void TheWholeMapFitsInUnderAMegabyte() {
		IconMap map = Load();

		Assert.True(map.EntryCount > 10_000, $"only indexed {map.EntryCount} entries");
		Assert.True(map.ApproximateBytes < 1024 * 1024,
			$"the map grew to {map.ApproximateBytes / 1024} KB");
	}
}
