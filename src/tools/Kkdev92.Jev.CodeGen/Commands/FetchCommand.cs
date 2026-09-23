using System.Globalization;
using System.Net;
using System.Text.Json;
using Kkdev92.Jev.CodeGen.Specifications;

namespace Kkdev92.Jev.CodeGen.Commands;

/// <summary>
/// <c>fetch</c>: the one command that uses the network. Downloads TypeSafe's document, extracts the
/// contract into <c>openapi.json</c>, records both fingerprints in <c>provenance.json</c>, and
/// nothing else.
/// </summary>
/// <remarks>
/// <para>
/// The downloaded document itself is not written anywhere: the repository keeps the contract and
/// the document's hash, not TypeSafe's prose (see <see cref="ContractExtractor"/>). To read what
/// changed in the descriptions, fetch the document yourself; its hash says whether it is the one
/// recorded.
/// </para>
/// <para>
/// It never touches <c>public-surface.json</c>, <c>semantics.json</c> or the generated sources.
/// Deciding what a changed contract means is a human's job, done in the pull request that
/// commits the new snapshot.
/// </para>
/// </remarks>
internal static class FetchCommand
{
    public const string DefaultSource = "https://api.typesafe.ai/openapi.json";

    /// <summary>A generous ceiling for a document of about 14 KB. A response past it is not a contract.</summary>
    internal const int MaxDocumentBytes = 4 * 1024 * 1024;

    public static async Task<int> RunAsync(string contract, string? sourceFile, string? retrievedAt, CancellationToken cancellationToken)
    {
        var layout = RepositoryLayout.Locate();
        var directory = layout.ContractDirectory(contract);
        Directory.CreateDirectory(directory);

        var provenancePath = Path.Combine(directory, Provenance.FileName);
        var source = File.Exists(provenancePath) ? Provenance.Read(provenancePath).Source : DefaultSource;

        byte[] raw;
        string timestamp;

        if (sourceFile is not null)
        {
            // Importing bytes that were downloaded earlier. The timestamp has to be supplied, because
            // the file's own modification time says when it was copied, not when it was fetched.
            if (retrievedAt is null)
            {
                Console.Error.WriteLine("codegen: --source-file needs --retrieved-at, the UTC time the file was downloaded.");
                return 1;
            }

            raw = await File.ReadAllBytesAsync(sourceFile, cancellationToken).ConfigureAwait(false);
            timestamp = Provenance.NormalizeTimestamp(retrievedAt);
        }
        else
        {
            (raw, var serverDate) = await DownloadAsync(new Uri(source), cancellationToken).ConfigureAwait(false);
            timestamp = retrievedAt is not null
                ? Provenance.NormalizeTimestamp(retrievedAt)
                : (serverDate ?? DateTimeOffset.UtcNow).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        }

        var (provenance, snapshot) = Import(contract, source, timestamp, raw);

        await File.WriteAllBytesAsync(Path.Combine(directory, Provenance.SnapshotFileName), snapshot, cancellationToken).ConfigureAwait(false);
        await File.WriteAllBytesAsync(provenancePath, provenance.ToJson(), cancellationToken).ConfigureAwait(false);

        Console.WriteLine($"fetch              : {source}");
        Console.WriteLine($"retrieved          : {provenance.RetrievedAtUtc}");
        Console.WriteLine($"openapi            : {provenance.OpenApiVersion}, API version {provenance.ApiVersion}");
        Console.WriteLine($"upstream           : {provenance.Upstream.Sha256} ({provenance.Upstream.ByteLength} bytes, not written)");
        Console.WriteLine($"snapshot           : {provenance.Snapshot.Sha256} ({provenance.Snapshot.ByteLength} bytes)");
        Console.WriteLine("next               : codegen generate, then review the diff before committing.");

        return 0;
    }

    /// <summary>Checks the downloaded bytes, extracts the contract and describes both. Throws rather than returning anything if they are not a usable document.</summary>
    internal static (Provenance Provenance, byte[] Snapshot) Import(string contract, string source, string retrievedAtUtc, byte[] raw)
    {
        var snapshot = ContractExtractor.Extract(raw);

        using var document = JsonDocument.Parse(snapshot);
        var root = document.RootElement;

        string Required(JsonElement element, string name)
            => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()!
                : throw new InvalidDataException($"The document has no string '{name}'. It does not look like an OpenAPI document.");

        var info = root.TryGetProperty("info", out var i) ? i : throw new InvalidDataException("The document has no 'info'.");

        var provenance = new Provenance(
            contract,
            source,
            retrievedAtUtc,
            Required(root, "openapi"),
            Required(info, "title"),
            Required(info, "version"),
            UpstreamFingerprint.Of(raw),
            FileFingerprint.Of(Provenance.SnapshotFileName, snapshot));

        return (provenance, snapshot);
    }

    /// <summary>Downloads the document under a size bound, and reports the server's own clock.</summary>
    internal static async Task<(byte[] Bytes, DateTimeOffset? Date)> DownloadAsync(Uri source, CancellationToken cancellationToken)
    {
        if (source.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException($"Refusing to fetch a contract over {source.Scheme}.");
        }

        using var handler = new SocketsHttpHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        using var response = await client.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode != HttpStatusCode.OK)
        {
            // A transient failure is reported, never mistaken for an empty contract.
            throw new InvalidOperationException($"GET {source} answered {(int)response.StatusCode}. Nothing was written.");
        }

        if (response.Content.Headers.ContentType?.MediaType != "application/json")
        {
            throw new InvalidOperationException($"GET {source} answered with '{response.Content.Headers.ContentType}', not application/json. Nothing was written.");
        }

        if (response.Content.Headers.ContentLength > MaxDocumentBytes)
        {
            throw new InvalidOperationException($"GET {source} declared {response.Content.Headers.ContentLength} bytes, over the {MaxDocumentBytes}-byte ceiling.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;

        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxDocumentBytes)
            {
                throw new InvalidOperationException($"GET {source} sent more than {MaxDocumentBytes} bytes.");
            }

            buffer.Write(chunk, 0, read);
        }

        return (buffer.ToArray(), response.Headers.Date);
    }
}
