using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.RegularExpressions;

namespace Kkdev92.Jev.Tests;

/// <summary>
/// Asserts that a packed package contains only what it is supposed to contain.
/// </summary>
/// <remarks>
/// <para>
/// <c>.gitignore</c> does not participate in packing. NuGet packs MSBuild items, so a file kept
/// out of git is not thereby kept out of the package: <c>Content</c> items are packed
/// automatically because <c>IncludeContentInPack</c> defaults to true, and any item can be packed
/// with <c>Pack="true"</c>. Local notes, tool configuration and anything else deliberately
/// untracked would be published without a word of warning.
/// </para>
/// <para>
/// So the guarantee is asserted here rather than assumed. The allowlist is the real protection:
/// anything not on it fails, whatever it happens to be. Reading each entry back for a build
/// machine's absolute path is defence in depth, for the case where a permitted file grows a
/// reference it should not have — a pdb path recorded in a debug directory, most often.
/// </para>
/// <para>
/// This runs against the output of <c>dotnet pack</c>, so it is excluded from the ordinary test
/// run and executed as its own step after packing. It skips rather than fails when no package is
/// present, so nobody is forced to pack before running the suite locally.
/// </para>
/// </remarks>
[Trait("Category", "Package")]
public sealed partial class PackageContentTests
{
    /// <summary>
    /// Exactly what each package carries, named rather than matched.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not a list of patterns. "Any <c>*.dll</c>, <c>*.xml</c> or <c>*.pdb</c> under
    /// <c>lib/net10.0/</c>" reads like an allowlist and admits a second assembly, or a resource
    /// file beside the intended one — so the claim that nothing can slip in and the check that
    /// makes it would disagree.
    /// </para>
    /// <para>
    /// The set is small and known, so it is written out. Both packages have the same shape: one
    /// assembly, its filtered documentation, the packaged readme and the two legal files. A
    /// package that grows an entry fails here and somebody decides whether it belongs, which is
    /// the whole point of having the test.
    /// </para>
    /// </remarks>
    private static string[] ExpectedEntries(string packageId) =>
    [
        $"{packageId}.nuspec",
        "README.md",
        "NOTICE",
        "LICENSE",
        "[Content_Types].xml",
        "_rels/.rels",
        $"lib/net10.0/{packageId}.dll",
        $"lib/net10.0/{packageId}.xml",
    ];

    /// <summary>Exactly what each symbols package carries: the pdb, beside NuGet's own parts.</summary>
    /// <remarks>
    /// It is pushed with the package and published to the symbol server, so it is held to a list
    /// the same way rather than only read for machine paths.
    /// </remarks>
    private static string[] ExpectedSymbolEntries(string packageId) =>
    [
        $"{packageId}.nuspec",
        "[Content_Types].xml",
        "_rels/.rels",
        $"lib/net10.0/{packageId}.pdb",
    ];

    /// <summary>The one entry NuGet names for itself, matched by location rather than by name.</summary>
    /// <remarks>
    /// NuGet has not always called this file the same thing: SDKs up to the 10.0.3xx band gave it a
    /// random hexadecimal name, and 10.0.4xx calls it <c>nuget.psmdcp</c>. This repository pins
    /// its SDK exactly in <c>global.json</c>, but a pin is moved on purpose from time to time, and
    /// a check keyed to one SDK's spelling would fail on the day it is. The part is identified by
    /// the OPC directory it has to live in, so whatever NuGet calls the file next is already
    /// covered — and the caller still asserts there is exactly one of them.
    /// </remarks>
    [GeneratedRegex(@"^package/services/metadata/core-properties/[^/]+\.psmdcp$")]
    private static partial Regex OpcCoreProperties();

    /// <summary>The package id, taken from the file name rather than guessed.</summary>
    [GeneratedRegex(@"^(?<id>.+?)\.\d+\.\d+\.\d+.*$")]
    private static partial Regex PackageFileName();

    /// <summary>
    /// An absolute path belonging to the machine that built the package.
    /// </summary>
    /// <remarks>
    /// <para>
    /// By shape, not by name. Listing the directories that happen to sit beside this repository —
    /// local notes, tool configuration, a benchmark output folder —
    /// would be both narrower and wider than the thing worth catching. Narrower, because a build
    /// on any other machine leaks <c>D:\src\…</c> and no name on such a list would notice. Wider,
    /// because such a name can only reach a package's <em>contents</em> as part of an absolute
    /// path, and the allowlist above already governs its entries — so naming them would add
    /// nothing this does not cover, while writing somebody's directory layout into a public file.
    /// </para>
    /// <para>
    /// A drive-lettered Windows path, a UNC share, or an absolute Unix path under a directory a
    /// build tree lives in all mean the same thing: something recorded about where the build ran
    /// rather than what it produced. For a build that claims to be deterministic that is a defect
    /// whoever's machine it names.
    /// </para>
    /// <para>
    /// The Windows half accepts either separator, because a path recorded by a cross-platform tool
    /// is as likely to arrive as <c>C:/src/…</c>. What it must not match is <c>/_/</c>, which is
    /// what a deterministic build normalises to, or an ordinary URL.
    /// </para>
    /// </remarks>
    [GeneratedRegex(
        @"[A-Za-z]:[\\/][^\\/:*?""<>|\r\n]+[\\/]"
        + @"|\\\\[A-Za-z0-9._-]+\\[A-Za-z0-9._$-]+\\"
        + @"|(?<![A-Za-z0-9.])/(?:home|Users|root|tmp|workspace|var/lib)/[A-Za-z0-9._-]+/")]
    private static partial Regex MachinePath();

    /// <summary>
    /// What counts as a build machine's path, and what does not.
    /// </summary>
    /// <remarks>
    /// The detector had no test of its own: a clean artefact passing said nothing about whether it
    /// could still find anything. These run without a package, so a change to the pattern fails
    /// here rather than the next time somebody packs locally by accident.
    /// </remarks>
    [Theory]
    [InlineData(@"C:\Projects\jev-dotnet\src\obj\x.pdb")]
    [InlineData(@"D:\src\whatever\bin\x.pdb")]
    [InlineData(@"C:/src/whatever/bin/x.pdb")]              // a cross-platform tool writes it this way
    [InlineData(@"\\build01\share\src\x.pdb")]              // a UNC share
    [InlineData("/home/runner/work/repo/x.pdb")]
    [InlineData("/Users/alice/dev/repo/x.pdb")]
    [InlineData("/root/build/repo/x.pdb")]
    [InlineData("/tmp/build-1234/repo/x.pdb")]
    [InlineData("/workspace/repo/x.pdb")]
    public void AMachinePathIsRecognised(string text)
        => Assert.True(MachinePath().IsMatch(text), $"'{text}' should have been recognised.");

    [Theory]
    [InlineData("/_/src/Kkdev92.Jev/JevClient.cs")]     // what a deterministic build records
    [InlineData("lib/net10.0/Kkdev92.Jev.dll")]
    [InlineData("https://api.typesafe.ai/v1/systemone")]
    [InlineData("answers/department/probabilities")]
    [InlineData("See https://docs.typesafe.ai/api for details.")]
    [InlineData("The ratio is 3:1 and rising")]
    public void OrdinaryTextIsNotAMachinePath(string text)
        => Assert.False(MachinePath().IsMatch(text), $"'{text}' should not have been recognised.");

    /// <summary>
    /// Six bytes of compiled noise that fit the pattern, kept as the reason compiled output is read
    /// structurally.
    /// </summary>
    /// <remarks>
    /// Sequences like this occur in real Portable PDBs, which is why a scan of whole files is
    /// unreliable. The detector is right to match it, which is the point: the pattern cannot tell
    /// six bytes of compiled noise from six characters of text, and no tightening of it would.
    /// Narrowing the pattern is the tempting fix for such a flake, so the thing that does not work
    /// is written down as an assertion rather than as advice.
    /// </remarks>
    [Fact]
    public void CompiledNoiseFitsTheShapeOfAPath()
        => Assert.Matches(MachinePath(), "o:/Q\u0001\\");

    /// <summary>
    /// A compiled entry goes to the structured readers rather than to the pattern.
    /// </summary>
    /// <remarks>
    /// Proved by handing them bytes that are not an image but do spell out a path. Refusing to
    /// parse is the evidence: were the dispatch ever to fall through, the call would succeed and
    /// report <c>C:\leaked\</c> instead. <see cref="EverythingElseIsStillReadWhole"/> holds the
    /// other half, that an entry which is genuinely text is still read whole.
    /// </remarks>
    [Theory]
    [InlineData("lib/net10.0/Kkdev92.Jev.dll")]
    [InlineData("lib/net10.0/Kkdev92.Jev.pdb")]
    public void CompiledOutputIsNeverDecodedAsText(string entryName)
        => Assert.Throws<BadImageFormatException>(
            () => RecordedPaths(entryName, Encoding.UTF8.GetBytes(@"C:\leaked\path\x.pdb")));

    [Theory]
    [InlineData("README.md")]
    [InlineData("Kkdev92.Jev.nuspec")]
    [InlineData("lib/net10.0/Kkdev92.Jev.xml")]
    public void EverythingElseIsStillReadWhole(string entryName)
    {
        var recorded = RecordedPaths(entryName, Encoding.UTF8.GetBytes(@"built in C:\src\repo\bin\"));

        Assert.Contains(recorded, r => MachinePath().IsMatch(r.Text));
    }

    [Theory]
    [InlineData("package/services/metadata/core-properties/nuget.psmdcp")]                  // 10.0.4xx
    [InlineData("package/services/metadata/core-properties/fdec5d6143fe4aec8ecb67fd73335f44.psmdcp")]
    public void NuGetsOwnMetadataPartIsRecognisedWhateverItIsCalled(string name)
        => Assert.True(OpcCoreProperties().IsMatch(name), $"'{name}' should have been recognised.");

    [Theory]
    [InlineData("lib/net10.0/Kkdev92.Jev.dll")]
    [InlineData("package/services/metadata/core-properties/nested/nuget.psmdcp")]
    [InlineData("some/other/place/nuget.psmdcp")]
    [InlineData("package/services/metadata/core-properties/nuget.nuspec")]
    public void NothingElseIsMistakenForIt(string name)
        => Assert.False(OpcCoreProperties().IsMatch(name), $"'{name}' should not have been recognised.");

    /// <summary>
    /// The packages to inspect, from wherever the last pack put them.
    /// </summary>
    /// <remarks>
    /// Searched rather than assumed. These tests skip when they find nothing, so a pack sent to
    /// some other output directory would otherwise leave them reporting success while inspecting
    /// nothing at all — which is worse than failing, because the run says the packages are clean.
    /// </remarks>
    private static string[] Packages()
    {
        string[] roots =
        [
            Path.Combine(RepositoryRoot.Value, "artifacts"),
            Path.Combine(RepositoryRoot.Value, "artifacts", "package", "release"),
            RepositoryRoot.Value,
        ];

        foreach (var root in roots.Where(Directory.Exists))
        {
            var found = Directory
                .EnumerateFiles(root, "*.nupkg", SearchOption.AllDirectories)
                .Concat(Directory.EnumerateFiles(root, "*.snupkg", SearchOption.AllDirectories))
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}local-feed{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .ToArray();

            if (found.Length > 0)
            {
                return found;
            }
        }

        return [];
    }

    [Fact]
    public void EveryPackageCarriesExactlyTheExpectedEntries()
    {
        var packages = Packages();

        Assert.SkipWhen(packages.Length == 0, "No package found; run 'dotnet pack -c Release -o artifacts' first.");

        foreach (var package in packages)
        {
            var name = Path.GetFileNameWithoutExtension(package);
            var match = PackageFileName().Match(name);

            Assert.True(match.Success, $"'{name}' is not a package file name.");

            var packageId = match.Groups["id"].Value;
            var symbols = package.EndsWith(".snupkg", StringComparison.OrdinalIgnoreCase);

            using var archive = ZipFile.OpenRead(package);

            var actual = archive.Entries
                .Select(e => e.FullName)
                .Where(n => !OpcCoreProperties().IsMatch(n))
                .Order(StringComparer.Ordinal)
                .ToArray();

            var expected = (symbols ? ExpectedSymbolEntries(packageId) : ExpectedEntries(packageId)).Order(StringComparer.Ordinal).ToArray();

            Assert.True(
                actual.SequenceEqual(expected, StringComparer.Ordinal),
                $"{Path.GetFileName(package)} does not carry exactly what it should.\n"
                + $"  unexpected: {string.Join(", ", actual.Except(expected, StringComparer.Ordinal))}\n"
                + $"  missing:    {string.Join(", ", expected.Except(actual, StringComparer.Ordinal))}");

            // The hashed one, exactly once.
            Assert.Single(archive.Entries, e => OpcCoreProperties().IsMatch(e.FullName));
        }
    }

    [Fact]
    public void NoPackedEntryMentionsALocalOnlyPath()
    {
        var packages = Packages();
        Assert.SkipWhen(packages.Length == 0, "No package found; run 'dotnet pack -c Release -o artifacts' first.");

        foreach (var package in packages)
        {
            using var archive = ZipFile.OpenRead(package);

            foreach (var entry in archive.Entries)
            {
                // The name as well as the contents. The exact-entry test above would refuse an entry
                // called after a build directory too; this one does not lean on it.
                if (MachinePath().Match(entry.FullName) is { Success: true } inName)
                {
                    Assert.Fail(
                        $"{Path.GetFileName(package)} has an entry named after the build machine's "
                        + $"path '{inName.Value}': '{entry.FullName}'.");
                }

                foreach (var (where, text) in RecordedPaths(entry.FullName, Contents(entry)))
                {
                    if (MachinePath().Match(text) is { Success: true } leaked)
                    {
                        Assert.Fail(
                            $"{Path.GetFileName(package)} entry '{entry.FullName}' carries the build "
                            + $"machine's path '{leaked.Value}' in its {where}. {Hint(entry.FullName)}");
                    }
                }
            }
        }
    }

    /// <summary>
    /// The structured readers find the fields they are aimed at, in the packages actually built.
    /// </summary>
    /// <remarks>
    /// Reading the right field is worth nothing if the field is not there. A format change, a
    /// handle read from the wrong table, or symbols switched off in the build would each leave the
    /// readers returning nothing — and the check above passing every time, on every package, while
    /// inspecting nothing at all. A check that cannot fail is the failure worth guarding against
    /// most, so it is asserted rather than assumed.
    /// </remarks>
    [Fact]
    public void EveryCompiledEntryRecordsAPathToCheck()
    {
        var packages = Packages();
        Assert.SkipWhen(packages.Length == 0, "No package found; run 'dotnet pack -c Release -o artifacts' first.");

        var inspected = new List<string>();

        foreach (var package in packages)
        {
            using var archive = ZipFile.OpenRead(package);

            foreach (var entry in archive.Entries)
            {
                if (!entry.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                    && !entry.FullName.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var recorded = RecordedPaths(entry.FullName, Contents(entry));

                Assert.True(
                    recorded.Count > 0 && recorded.TrueForAll(r => !string.IsNullOrWhiteSpace(r.Text)),
                    $"{Path.GetFileName(package)} entry '{entry.FullName}' recorded no path to check, "
                    + "so nothing about it was verified.");

                inspected.Add(entry.FullName);
            }
        }

        // Both kinds, not just whichever happened to be present. The symbol packages go through a
        // different reader from the assemblies, and finding only one of them would leave the other
        // untested while this still passed.
        Assert.Contains(inspected, name => name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(inspected, name => name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase));
    }

    private static byte[] Contents(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>
    /// The paths a packed entry records, read out of the places that hold paths.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Decoding every entry as UTF-8 and running the pattern over the whole thing, compiled output
    /// included, does not work. It finds a path in a pdb on one commit and not on the next,
    /// because what it finds is compiled noise — <c>o:/Q</c>, an unprintable byte, a backslash —
    /// that happens to fit the shape of a drive-lettered path. A pattern loose enough to catch
    /// <c>D:\src\…</c> is loose enough to catch that, and there is no tightening that fixes it:
    /// over hundreds of kilobytes of binary, the shape turns up by chance.
    /// </para>
    /// <para>
    /// What makes it worse than an ordinary flake is that it is re-rolled on every commit. A pdb
    /// carries the commit hash, through SourceLink and through the content hash the deterministic
    /// build derives its identifiers from, so each commit rewrites the bytes and rolls again — and
    /// the build that finally fails can be one that changed a comment in a workflow file.
    /// </para>
    /// <para>
    /// So a compiled entry is now read as what it is. An assembly records the path of its symbol
    /// file in the CodeView entry of the debug directory, and a Portable PDB records the source
    /// files in its document table — those are the two places a build machine's path reaches a
    /// package, and they are read through <c>System.Reflection.Metadata</c> rather than guessed at.
    /// Everything else in a package is genuinely text, and is still scanned whole.
    /// </para>
    /// </remarks>
    private static List<(string Where, string Text)> RecordedPaths(string entryName, byte[] content)
    {
        var found = new List<(string Where, string Text)>();

        if (entryName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            using var assembly = new PEReader(new MemoryStream(content));

            foreach (var directoryEntry in assembly.ReadDebugDirectory())
            {
                if (directoryEntry.Type == DebugDirectoryEntryType.CodeView)
                {
                    found.Add(("debug directory", assembly.ReadCodeViewDebugDirectoryData(directoryEntry).Path));
                }

                // Not how this repository builds — the symbols ship in a .snupkg — but flipping
                // DebugType to embedded would otherwise move every source path inside the assembly
                // and out of the reach of this test without anything saying so.
                if (directoryEntry.Type == DebugDirectoryEntryType.EmbeddedPortablePdb)
                {
                    using var embedded = assembly.ReadEmbeddedPortablePdbDebugDirectoryData(directoryEntry);
                    found.AddRange(SourceFiles(embedded.GetMetadataReader()).Select(f => ("embedded pdb", f)));
                }
            }

            return found;
        }

        if (entryName.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase))
        {
            using var symbols = MetadataReaderProvider.FromPortablePdbStream(new MemoryStream(content));
            found.AddRange(SourceFiles(symbols.GetMetadataReader()).Select(f => ("document table", f)));

            return found;
        }

        found.Add(("contents", Encoding.UTF8.GetString(content)));

        return found;
    }

    /// <summary>The source files a Portable PDB says it describes.</summary>
    private static List<string> SourceFiles(MetadataReader reader)
        => [.. reader.Documents.Select(handle => reader.GetString(reader.GetDocument(handle).Name))];

    /// <summary>
    /// Explains the failure most likely to be seen, so it is actionable rather than alarming.
    /// </summary>
    /// <remarks>
    /// A build without <c>ContinuousIntegrationBuild</c> records the machine's own paths in both
    /// places this test reads: the pdb path in the assembly's debug directory, and the source file
    /// names in the symbol file. Either spells out a maintainer's directory layout. They are
    /// normalised to <c>/_/</c> only when the property is set, and this repository sets it from the
    /// <c>CI</c> environment variable, so a local <c>dotnet pack</c> produces a package that must
    /// not be published. The caller has already named which of the two it found.
    /// </remarks>
    private static string Hint(string entryName)
        => entryName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            || entryName.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)
            ? "This is what a build made without ContinuousIntegrationBuild records. Rebuild and "
              + "repack with CI=true, which is what the release workflow does, rather than "
              + "publishing this artefact."
            : string.Empty;

    /// <summary>
    /// Every package carries its legal files, and they are the ones in the repository.
    /// </summary>
    /// <remarks>
    /// The allowlist above says these <em>may</em> be present, which is a different statement:
    /// deleting the pack settings that put them there would leave every package legally
    /// incomplete and every test still green. Content is compared as well as presence, because a
    /// license that has drifted from the one the repository publishes under is the same problem
    /// as a missing one.
    /// </remarks>
    [Fact]
    public void EveryPackageCarriesTheLegalFilesFromTheRepository()
    {
        var packages = Packages().Where(p => p.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase)).ToArray();
        Assert.SkipWhen(packages.Length == 0, "No package found; run 'dotnet pack -c Release -o artifacts' first.");

        // NOTICE carries the attribution for the TypeSafe material the contract derives from and the
        // non-affiliation statement, and LICENSE is the license the metadata names. README.md is on
        // this list because it is where a consumer on nuget.org is told that this is not an
        // official TypeSafe SDK; checking that it exists would not notice that being edited away.
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["LICENSE"] = ReadRepositoryFile("LICENSE"),
            ["NOTICE"] = ReadRepositoryFile("NOTICE"),
            ["README.md"] = ReadRepositoryFile(Path.Combine("eng", "PACKAGE-README.md")),
        };

        static string ReadRepositoryFile(string relativePath)
            => File.ReadAllText(Path.Combine(RepositoryRoot.Value, relativePath)).ReplaceLineEndings("\n");

        foreach (var package in packages)
        {
            using var archive = ZipFile.OpenRead(package);
            var names = archive.Entries.Select(e => e.FullName).ToArray();

            foreach (var (name, original) in expected)
            {
                Assert.True(names.Contains(name), $"{Path.GetFileName(package)} carries no {name}.");

                using var reader = new StreamReader(archive.GetEntry(name)!.Open());

                Assert.Equal(original, reader.ReadToEnd().ReplaceLineEndings("\n"));
            }
        }
    }

    [Fact]
    public void ThePackagedReadmeHasNoRelativeLinks()
    {
        var packages = Packages().Where(p => p.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase)).ToArray();
        Assert.SkipWhen(packages.Length == 0, "No package found; run 'dotnet pack -c Release -o artifacts' first.");

        foreach (var package in packages)
        {
            using var archive = ZipFile.OpenRead(package);
            using var reader = new StreamReader(archive.GetEntry("README.md")!.Open());
            var markdown = reader.ReadToEnd();

            // nuget.org resolves a relative link against nuget.org, so every one of them is a 404
            // on the package page. This is why the packaged readme is a separate file.
            var relative = System.Text.RegularExpressions.Regex
                .Matches(markdown, @"\]\(([^)]+)\)")
                .Select(m => m.Groups[1].Value)
                .Where(t => !t.StartsWith("http://", StringComparison.Ordinal)
                         && !t.StartsWith("https://", StringComparison.Ordinal)
                         && !t.StartsWith('#'))
                .ToArray();

            Assert.Empty(relative);
        }
    }

    /// <summary>
    /// The packaged documentation covers the public surface and nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The compiler documents whatever carries a <c>///</c> comment, and this repository comments
    /// private members on purpose — the reasoning belongs next to the code it explains. Microsoft's
    /// own guidance says documenting private members "exposes the inner (potentially confidential)
    /// workings of your library", and there is no compiler switch for it, so the file is filtered
    /// before it is packed.
    /// </para>
    /// <para>
    /// Read from the package rather than from the build output, because the package is the thing
    /// that ships and the filtering happens between the two.
    /// </para>
    /// </remarks>
    [Fact]
    public void ThePackagedDocumentationDescribesOnlyWhatAConsumerCanSee()
    {
        var packages = Packages()
            .Where(p => p.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.SkipWhen(packages.Length == 0, "No package found; run 'dotnet pack -c Release -o artifacts' first.");

        foreach (var package in packages)
        {
            using var archive = ZipFile.OpenRead(package);

            var entry = archive.Entries.SingleOrDefault(e =>
                e.FullName.StartsWith("lib/", StringComparison.Ordinal)
                && e.FullName.EndsWith(".xml", StringComparison.Ordinal));

            Assert.NotNull(entry);

            using var stream = entry.Open();
            var documented = System.Xml.Linq.XDocument.Load(stream)
                .Descendants("member")
                .Select(member => (string?)member.Attribute("name") ?? string.Empty)
                .ToArray();

            Assert.NotEmpty(documented);

            // A private field is the clearest case. The client's _gate and the answer views'
            // _probabilities are commented because the reasoning belongs beside them, and none of
            // it is anything a consumer can reach.
            var privateFields = documented
                .Where(id => id.StartsWith("F:", StringComparison.Ordinal))
                .Where(id => id.Split('.')[^1].StartsWith('_'))
                .ToArray();

            Assert.Empty(privateFields);

            // Nothing in the library uses the regex source generator today. If a pattern is ever
            // added, the generator emits an internal type per pattern with documented static
            // fields, none of it reachable and all of it naming internals.
            var generatedInternals = documented
                .Where(id => id.Contains("System.Text.RegularExpressions.Generated", StringComparison.Ordinal))
                .ToArray();

            Assert.Empty(generatedInternals);
        }
    }

    /// <summary>
    /// A public generic method keeps its documentation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The filter compares a documentation id against the members the assembly declares, and an id
    /// for a generic method carries its arity: <c>AddChoice``1</c> against a member table that
    /// says <c>AddChoice</c>. Compared literally, every public generic method looks like a member
    /// the assembly does not have, and its documentation is removed.
    /// </para>
    /// <para>
    /// Which is the exact failure the filter exists to avoid, pointed the other way — and it is
    /// invisible in the package: a consumer hovering over <c>AddChoice&lt;T&gt;</c> gets nothing,
    /// and nothing anywhere says why.
    /// </para>
    /// </remarks>
    [Fact]
    public void ThePackagedDocumentationKeepsPublicGenericMethods()
    {
        var packages = Packages()
            .Where(p => p.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase))
            .Where(p => Path.GetFileName(p).StartsWith("Kkdev92.Jev.0", StringComparison.Ordinal))
            .ToArray();

        Assert.SkipWhen(packages.Length == 0, "No package found; run 'dotnet pack -c Release -o artifacts' first.");

        foreach (var package in packages)
        {
            using var archive = ZipFile.OpenRead(package);

            var entry = archive.Entries.Single(e =>
                e.FullName.StartsWith("lib/", StringComparison.Ordinal)
                && e.FullName.EndsWith(".xml", StringComparison.Ordinal));

            using var stream = entry.Open();
            var documented = System.Xml.Linq.XDocument.Load(stream)
                .Descendants("member")
                .Select(member => (string?)member.Attribute("name") ?? string.Empty)
                .ToArray();

            // Every public generic method the core package has, each the kind of member a caller
            // reaches for and then wants to read about: typed state, typed content, a typed choice
            // and the typed answer that comes back for it.
            string[] expected =
            [
                "M:Kkdev92.Jev.JevClient.EvaluateAsync``1",
                "M:Kkdev92.Jev.JevContent.FromJson``1",
                "M:Kkdev92.Jev.JevDecisionPlanBuilder.AddChoice``1",
                "M:Kkdev92.Jev.JevResult.Get``1",
            ];

            foreach (var member in expected)
            {
                Assert.Contains(documented, id => id.StartsWith(member, StringComparison.Ordinal));
            }
        }
    }
}
