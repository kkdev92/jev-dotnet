using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace Kkdev92.Jev.CodeGen.Specifications;

/// <summary>Where a snapshot came from, and the hashes that tie the committed contract to it.</summary>
/// <remarks>
/// <para>
/// Facts only. Nothing here is a judgement, which is why it can be rewritten wholesale by
/// <c>codegen fetch</c> while <c>public-surface.json</c> and <c>semantics.json</c> never are.
/// </para>
/// <para>
/// Two fingerprints, because there are two documents. <see cref="Upstream"/> is TypeSafe's document
/// exactly as the endpoint served it, which is not committed (see <see cref="ContractExtractor"/>);
/// its hash is how anybody holding a copy can check it is the one the contract came from.
/// <see cref="Snapshot"/> is <c>openapi.json</c>, the contract extracted from it, which is committed
/// and is what the generator reads.
/// </para>
/// </remarks>
internal sealed record Provenance(
    string Contract,
    string Source,
    string RetrievedAtUtc,
    string OpenApiVersion,
    string ApiTitle,
    string ApiVersion,
    UpstreamFingerprint Upstream,
    FileFingerprint Snapshot)
{
    public const string FileName = "provenance.json";

    /// <summary>The contract the generator reads.</summary>
    public const string SnapshotFileName = "openapi.json";

    /// <summary>
    /// What a copy of the upstream document is called when somebody saves one beside the contract:
    /// the name the contributing guide warns about, and the name it had before the document stopped
    /// being committed.
    /// </summary>
    /// <remarks>Ignored by git, and refused by the loader, so that neither can be committed by accident.</remarks>
    public static readonly string[] UpstreamCopyNames = ["openapi.upstream.json", "openapi.raw.json"];

    public static Provenance Read(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { AllowDuplicateProperties = false });
        var root = document.RootElement;

        return new Provenance(
            Contract: RequiredString(root, "contract"),
            Source: RequiredString(root, "source"),
            RetrievedAtUtc: RequiredString(root, "retrievedAtUtc"),
            OpenApiVersion: RequiredString(root, "openapi"),
            ApiTitle: RequiredString(root, "title"),
            ApiVersion: RequiredString(root, "infoVersion"),
            Upstream: UpstreamFingerprint.Read(Required(root, "upstream")),
            Snapshot: FileFingerprint.Read(Required(root, "snapshot")));
    }

    public byte[] ToJson()
    {
        var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, IndentSize = 2, NewLine = "\n", Encoder = JsonCanonicalizer.Encoder }))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("$comment");
            writer.WriteStringValue("Facts about where the snapshot came from. Rewritten by 'codegen fetch'; never hand-edited.");
            writer.WriteStringValue("upstream describes TypeSafe's document exactly as the endpoint served it. It is not committed: TypeSafe's");
            writer.WriteStringValue("terms of use do not permit redistributing it, and its hash is enough to identify it. snapshot describes");
            writer.WriteStringValue("openapi.json, the contract extracted from it: every structural keyword and none of the descriptions, titles");
            writer.WriteStringValue("or examples, with keys sorted ordinally, two-space indent, LF and a trailing newline.");
            writer.WriteStringValue("retrievedAtUtc is provenance only and must never reach generated C#.");
            writer.WriteEndArray();
            writer.WriteString("contract", Contract);
            writer.WriteString("source", Source);
            writer.WriteString("retrievedAtUtc", RetrievedAtUtc);
            writer.WriteString("openapi", OpenApiVersion);
            writer.WriteString("title", ApiTitle);
            writer.WriteString("infoVersion", ApiVersion);
            writer.WritePropertyName("upstream");
            Upstream.Write(writer);
            writer.WritePropertyName("snapshot");
            Snapshot.Write(writer);
            writer.WriteEndObject();
        }

        buffer.WriteByte((byte)'\n');
        return buffer.ToArray();
    }

    /// <summary>Validates an ISO 8601 UTC timestamp of the form <c>yyyy-MM-ddTHH:mm:ssZ</c>.</summary>
    public static string NormalizeTimestamp(string value)
    {
        if (!DateTimeOffset.TryParseExact(value, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            throw new ArgumentException($"'{value}' is not a UTC timestamp of the form 2026-09-22T21:13:07Z.", nameof(value));
        }

        return parsed.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    }

    private static JsonElement Required(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : throw new InvalidDataException($"{FileName} has no object '{name}'.");

    private static string RequiredString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new InvalidDataException($"{FileName} has no string '{name}'.");
}

/// <summary>The SHA-256 and length of the document as the endpoint served it.</summary>
internal sealed record UpstreamFingerprint(string Sha256, long ByteLength)
{
    public static UpstreamFingerprint Of(ReadOnlySpan<byte> bytes)
        => new(Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length);

    public static UpstreamFingerprint Read(JsonElement element)
        => new(
            element.GetProperty("sha256").GetString() ?? throw new InvalidDataException("The upstream fingerprint has no sha256."),
            element.GetProperty("byteLength").GetInt64());

    public void Write(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteString("sha256", Sha256);
        writer.WriteNumber("byteLength", ByteLength);
        writer.WriteEndObject();
    }
}

/// <summary>The SHA-256 and length of one committed file.</summary>
internal sealed record FileFingerprint(string File, string Sha256, long ByteLength)
{
    public static FileFingerprint Of(string fileName, ReadOnlySpan<byte> bytes)
        => new(fileName, Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length);

    public static FileFingerprint Read(JsonElement element)
        => new(
            element.GetProperty("file").GetString() ?? throw new InvalidDataException("A fingerprint has no file name."),
            element.GetProperty("sha256").GetString() ?? throw new InvalidDataException("A fingerprint has no sha256."),
            element.GetProperty("byteLength").GetInt64());

    public void Write(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteString("file", File);
        writer.WriteString("sha256", Sha256);
        writer.WriteNumber("byteLength", ByteLength);
        writer.WriteEndObject();
    }
}
