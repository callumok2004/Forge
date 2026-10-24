using DWrite = Vortice.DirectWrite;

namespace Forge.Render;

public sealed class BundledFonts : IDisposable
{
	private static readonly string[] Files = ["geist.ttf", "jetbrains-mono.ttf"];

	public BundledFonts(DWrite.IDWriteFactory factory, string directory) {
		using DWrite.IDWriteFactory5? factory5 = factory.QueryInterfaceOrNull<DWrite.IDWriteFactory5>();

		if (factory5 is null) return;

		using DWrite.IDWriteFontSetBuilder1 builder = factory5.CreateFontSetBuilder();

		foreach (string file in Files) {
			string path = Path.Combine(directory, file);

			if (!File.Exists(path)) continue;

			using DWrite.IDWriteFontFile reference = factory.CreateFontFileReference(path);
			builder.AddFontFile(reference);
		}

		using DWrite.IDWriteFontSet set = builder.CreateFontSet();
		Collection = factory5.CreateFontCollectionFromFontSet(set);
	}

	public DWrite.IDWriteFontCollection? Collection { get; private set; }

	public void Dispose() {
		Collection?.Dispose();
		Collection = null;
	}
}
