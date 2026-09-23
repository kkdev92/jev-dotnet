using System.Text.Json;
using System.Text.Json.Nodes;
using Kkdev92.Jev.CodeGen.Specifications;

namespace Kkdev92.Jev.CodeGen.Tests;

/// <summary>The committed snapshot, and copies of it with one thing changed.</summary>
internal static class SpecFixture
{
    public static RepositoryLayout Layout { get; } = RepositoryLayout.At(Locate());

    public static SpecSnapshot Committed() => SpecLoader.Load(Layout, RepositoryLayout.DefaultContract);

    /// <summary>The committed document as a mutable tree.</summary>
    public static JsonObject Document() => JsonNode.Parse(Committed().CanonicalBytes)!.AsObject();

    /// <summary>A snapshot whose document has been edited, with every companion file as committed.</summary>
    /// <remarks>
    /// The provenance is recomputed, so the edit is what is under test and not the hash check. The
    /// edited document is canonicalized but not extracted: a test that adds a description is asking
    /// whether the parser reads past it, and extraction would remove it first.
    /// </remarks>
    public static SpecSnapshot With(Action<JsonObject> edit)
    {
        var document = Document();
        edit(document);

        var upstream = JsonSerializer.SerializeToUtf8Bytes(document);
        var canonical = JsonCanonicalizer.Canonicalize(upstream);
        var committed = Committed();

        return committed with
        {
            CanonicalBytes = canonical,
            Provenance = committed.Provenance with
            {
                Upstream = UpstreamFingerprint.Of(upstream),
                Snapshot = FileFingerprint.Of(Provenance.SnapshotFileName, canonical),
            },
        };
    }

    public static JsonObject Schema(this JsonObject document, string name) => document["components"]!["schemas"]![name]!.AsObject();

    public static JsonObject Property(this JsonObject document, string schema, string property) => document.Schema(schema)["properties"]![property]!.AsObject();

    private static string Locate()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (RepositoryLayout.IsRoot(directory.FullName))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root.");
    }
}
