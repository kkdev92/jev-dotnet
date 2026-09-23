using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Kkdev92.Jev.CodeGen.Specifications;

namespace Kkdev92.Jev.CodeGen.Tests;

/// <summary>The committed snapshot is exactly what its provenance says it is, and nothing more.</summary>
public sealed class SpecSnapshotTests
{
    private static string ContractDirectory => SpecFixture.Layout.ContractDirectory(RepositoryLayout.DefaultContract);

    private static byte[] SnapshotBytes => File.ReadAllBytes(Path.Combine(ContractDirectory, Provenance.SnapshotFileName));

    [Fact]
    public void TheCommittedSnapshotLoadsAndVerifies()
    {
        var snapshot = SpecFixture.Committed();

        Assert.Equal("3.1.0", snapshot.Provenance.OpenApiVersion);
        Assert.Equal("TypeSafe", snapshot.Provenance.ApiTitle);
        Assert.Equal("https://api.typesafe.ai/openapi.json", snapshot.Provenance.Source);
    }

    /// <summary>Computed here, independently of the loader, so a bug in one cannot vouch for the other.</summary>
    [Fact]
    public void TheSnapshotHashesToTheRecordedValue()
    {
        var provenance = Provenance.Read(Path.Combine(ContractDirectory, Provenance.FileName));
        var snapshot = SnapshotBytes;

        Assert.Equal(provenance.Snapshot.Sha256, Convert.ToHexStringLower(SHA256.HashData(snapshot)));
        Assert.Equal(provenance.Snapshot.ByteLength, snapshot.Length);
    }

    [Fact]
    public void TheUpstreamFingerprintIsAWellFormedSha256()
    {
        var provenance = Provenance.Read(Path.Combine(ContractDirectory, Provenance.FileName));

        Assert.Matches("^[0-9a-f]{64}$", provenance.Upstream.Sha256);
        Assert.True(provenance.Upstream.ByteLength > 0);
    }

    [Fact]
    public void TheSnapshotIsAnExtractedCanonicalContract()
    {
        var snapshot = SnapshotBytes;

        Assert.True(JsonCanonicalizer.IsCanonical(snapshot));
        Assert.True(ContractExtractor.IsContract(snapshot));
    }

    /// <summary>
    /// No annotation survives anywhere it would be an annotation — checked by a rule written for this
    /// document, independently of the extractor's own.
    /// </summary>
    /// <remarks>
    /// In this document the only place a member may be called <c>description</c> or <c>title</c>
    /// without being an annotation is directly inside <c>properties</c> — <c>ModelMetadata</c> has a
    /// field called <c>description</c> — and <c>info.title</c>, which OpenAPI requires. Anything
    /// else with one of those names is TypeSafe's prose, and must not be in the repository.
    /// </remarks>
    [Fact]
    public void NoAnnotationTextIsCommitted()
    {
        using var document = JsonDocument.Parse(SnapshotBytes);
        var found = new List<string>();

        void Walk(JsonElement element, string path, string? parentKey)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var member in element.EnumerateObject())
                    {
                        var isAnnotationName = ContractExtractor.AnnotationKeywords.Contains(member.Name);
                        var isAllowed = parentKey == "properties" || (path == "/info" && member.Name == "title");

                        if (isAnnotationName && !isAllowed)
                        {
                            found.Add($"{path}/{member.Name}");
                        }

                        Walk(member.Value, $"{path}/{member.Name}", member.Name);
                    }

                    break;

                case JsonValueKind.Array:
                    var index = 0;

                    foreach (var item in element.EnumerateArray())
                    {
                        Walk(item, $"{path}/{index++}", parentKey: null);
                    }

                    break;
            }
        }

        Walk(document.RootElement, string.Empty, parentKey: null);

        Assert.Empty(found);
    }

    /// <summary>The field that shares a name with an annotation is still part of the contract.</summary>
    [Fact]
    public void AFieldNamedLikeAnAnnotationSurvives()
    {
        using var document = JsonDocument.Parse(SnapshotBytes);
        var metadata = document.RootElement.GetProperty("components").GetProperty("schemas").GetProperty("ModelMetadata");

        Assert.True(metadata.GetProperty("properties").TryGetProperty("description", out _));
        Assert.Contains(metadata.GetProperty("required").EnumerateArray(), e => e.GetString() == "description");
    }

    /// <summary>
    /// The contract directory holds the contract and its companion files, and not TypeSafe's document.
    /// </summary>
    [Fact]
    public void OnlyTheContractAndItsCompanionFilesAreCommitted()
    {
        var files = Directory.EnumerateFiles(ContractDirectory).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(
            [NamingOverrides.FileName, Provenance.SnapshotFileName, Provenance.FileName, PublicSurface.FileName, Semantics.FileName],
            files);
    }

    [Fact]
    public void ACopyOfTheUpstreamDocumentBesideTheContractStopsTheLoader()
    {
        var root = Directory.CreateTempSubdirectory("jev-spec-");

        try
        {
            var layout = RepositoryLayout.At(root.FullName);
            var directory = layout.ContractDirectory(RepositoryLayout.DefaultContract);
            Directory.CreateDirectory(directory);

            foreach (var file in Directory.EnumerateFiles(ContractDirectory))
            {
                File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
            }

            _ = SpecLoader.Load(layout, RepositoryLayout.DefaultContract);

            foreach (var name in Provenance.UpstreamCopyNames)
            {
                File.WriteAllText(Path.Combine(directory, name), "{}");
                Assert.Throws<InvalidDataException>(() => SpecLoader.Load(layout, RepositoryLayout.DefaultContract));
                File.Delete(Path.Combine(directory, name));
            }
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void TheSnapshotIsLfUtf8WithoutABomAndEndsWithANewline()
    {
        var snapshot = SnapshotBytes;

        Assert.DoesNotContain((byte)'\r', snapshot);
        Assert.False(snapshot.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]));
        Assert.Equal((byte)'\n', snapshot[^1]);
    }

    [Fact]
    public void TheRetrievalTimeIsAUtcTimestamp()
    {
        var provenance = Provenance.Read(Path.Combine(ContractDirectory, Provenance.FileName));

        Assert.True(DateTimeOffset.TryParseExact(provenance.RetrievedAtUtc, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _));
    }

    [Fact]
    public void AFailedCheckIsReportedNotRepaired()
    {
        var snapshot = SpecFixture.Committed();

        var tampered = snapshot.CanonicalBytes.ToArray();
        tampered[^2] = (byte)' ';
        Assert.Throws<InvalidDataException>(() => SpecLoader.Verify(snapshot.Contract, snapshot.Provenance, tampered));

        Assert.Throws<InvalidDataException>(() => SpecLoader.Verify("another-contract", snapshot.Provenance, snapshot.CanonicalBytes));

        // A description put back by hand, with the provenance updated to match, still fails: the
        // hash only proves the file is the recorded one, not that the recorded one is a contract.
        var annotated = SpecFixture.With(d => d.Schema("Usage")["description"] = "Token usage for the request.");
        Assert.Throws<InvalidDataException>(() => SpecLoader.Verify(annotated.Contract, annotated.Provenance, annotated.CanonicalBytes));
    }

    [Fact]
    public void EveryCompanionFileIsStrictJson()
    {
        foreach (var file in Directory.EnumerateFiles(ContractDirectory, "*.json"))
        {
            using var document = SpecJson.Parse(file);
            Assert.NotEqual(JsonValueKind.Undefined, document.RootElement.ValueKind);
        }
    }
}
