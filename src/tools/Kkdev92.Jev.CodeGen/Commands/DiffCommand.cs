using Kkdev92.Jev.CodeGen.IntermediateModel;
using Kkdev92.Jev.CodeGen.OpenApi;
using Kkdev92.Jev.CodeGen.Specifications;
using Kkdev92.Jev.CodeGen.Validation;

namespace Kkdev92.Jev.CodeGen.Commands;

/// <summary>
/// <c>diff</c>: compares the committed snapshot with another document and classifies what moved.
/// </summary>
/// <remarks>
/// <para>
/// It reports and never writes. <c>spec-check.yml</c> runs it against the live endpoint when a
/// person starts it, and opens an issue when it finds something; acting on it stays a reviewed
/// change.
/// </para>
/// <para>
/// Three levels, cheapest first. Bytes identical to the recorded upstream fingerprint: nothing
/// changed. Bytes different but the extracted contract identical: only descriptions, titles,
/// examples or layout moved, and the generated code would not. Otherwise the two contracts are
/// parsed and every difference is classified.
/// </para>
/// <para>
/// Whether a change breaks this SDK depends on which way the data flows. A new required property
/// on something the SDK sends is breaking — requests start failing — while the same change on
/// something it receives is not. So every schema is classified by whether it is reachable from a
/// request body, a response body, or both, and each change is judged from that side.
/// </para>
/// </remarks>
internal static class DiffCommand
{
    public static async Task<int> RunAsync(string contract, string? againstFile, CancellationToken cancellationToken)
    {
        var layout = RepositoryLayout.Locate();
        var committed = SpecLoader.Load(layout, contract);

        var candidate = againstFile is null
            ? (await FetchCommand.DownloadAsync(new Uri(committed.Provenance.Source), cancellationToken).ConfigureAwait(false)).Bytes
            : await File.ReadAllBytesAsync(againstFile, cancellationToken).ConfigureAwait(false);

        foreach (var line in Compare(committed, candidate).Lines())
        {
            Console.WriteLine(line);
        }

        // Reporting is the job. Whether a report is worth an issue is decided by the caller.
        return 0;
    }

    internal static DiffReport Compare(SpecSnapshot committed, byte[] candidateRaw)
    {
        var additive = new List<string>();
        var breaking = new List<string>();
        var notes = new List<string>();

        var upstream = UpstreamFingerprint.Of(candidateRaw);
        var committedSnapshot = committed.Provenance.Snapshot.Sha256;

        if (upstream == committed.Provenance.Upstream)
        {
            return new DiffReport(committed.Provenance.Upstream.Sha256, upstream.Sha256, committedSnapshot, committedSnapshot, additive, breaking, notes);
        }

        byte[] candidateCanonical;

        try
        {
            candidateCanonical = ContractExtractor.Extract(candidateRaw);
        }
        catch (InvalidDataException ex)
        {
            breaking.Add($"the candidate is not a usable document: {ex.Message}");
            return new DiffReport(committed.Provenance.Upstream.Sha256, upstream.Sha256, committedSnapshot, "(unreadable)", additive, breaking, notes);
        }

        var candidateFingerprint = FileFingerprint.Of(Provenance.SnapshotFileName, candidateCanonical);

        if (candidateCanonical.AsSpan().SequenceEqual(committed.CanonicalBytes))
        {
            return new DiffReport(committed.Provenance.Upstream.Sha256, upstream.Sha256, committedSnapshot, candidateFingerprint.Sha256, additive, breaking, notes);
        }

        var before = OpenApiParser.Parse(committed);
        ApiContract after;

        var candidateSnapshot = committed with
        {
            CanonicalBytes = candidateCanonical,
            Provenance = committed.Provenance with
            {
                Upstream = upstream,
                Snapshot = candidateFingerprint,
            },
        };

        try
        {
            after = OpenApiParser.Parse(candidateSnapshot);
        }
        catch (Exception ex) when (ex is UnsupportedSpecificationException or InvalidDataException)
        {
            breaking.Add($"the candidate uses something the generator does not interpret: {ex.Message}");
            return new DiffReport(committed.Provenance.Upstream.Sha256, upstream.Sha256, committedSnapshot, candidateFingerprint.Sha256, additive, breaking, notes);
        }

        if (before.ApiVersion != after.ApiVersion)
        {
            notes.Add($"info.version moved from {before.ApiVersion} to {after.ApiVersion}");
        }

        CompareOperations(before, after, additive, breaking);
        CompareSchemas(before, after, additive, breaking);
        CompareSemantics(candidateSnapshot, breaking);

        return new DiffReport(committed.Provenance.Upstream.Sha256, upstream.Sha256, committedSnapshot, candidateFingerprint.Sha256, additive, breaking, notes);
    }

    private static void CompareOperations(ApiContract before, ApiContract after, List<string> additive, List<string> breaking)
    {
        var old = before.Operations.ToDictionary(o => o.Key);
        var current = after.Operations.ToDictionary(o => o.Key);

        foreach (var key in current.Keys.Except(old.Keys).OrderBy(k => k.ToString(), StringComparer.Ordinal))
        {
            additive.Add($"new operation {key} — generation stops until public-surface.json approves or excludes it");
        }

        foreach (var key in old.Keys.Except(current.Keys).OrderBy(k => k.ToString(), StringComparer.Ordinal))
        {
            breaking.Add($"operation {key} was removed");
        }

        foreach (var key in old.Keys.Intersect(current.Keys).OrderBy(k => k.ToString(), StringComparer.Ordinal))
        {
            var (a, b) = (old[key], current[key]);

            if (a.RequestSchema != b.RequestSchema || a.RequestRequired != b.RequestRequired)
            {
                breaking.Add($"{key}: the request body changed from {a.RequestSchema ?? "none"} to {b.RequestSchema ?? "none"}");
            }

            if (!a.SecuritySchemes.SequenceEqual(b.SecuritySchemes, StringComparer.Ordinal))
            {
                breaking.Add($"{key}: security changed from [{string.Join(", ", a.SecuritySchemes)}] to [{string.Join(", ", b.SecuritySchemes)}]");
            }

            var responsesBefore = a.Responses.ToDictionary(r => r.Status, r => r.Schema, StringComparer.Ordinal);
            var responsesAfter = b.Responses.ToDictionary(r => r.Status, r => r.Schema, StringComparer.Ordinal);

            foreach (var status in responsesAfter.Keys.Except(responsesBefore.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                additive.Add($"{key}: now documents a {status} response");
            }

            foreach (var status in responsesBefore.Keys.Except(responsesAfter.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                breaking.Add($"{key}: no longer documents a {status} response");
            }

            foreach (var status in responsesBefore.Keys.Intersect(responsesAfter.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                if (responsesBefore[status] != responsesAfter[status])
                {
                    breaking.Add($"{key}: the {status} body changed from {responsesBefore[status] ?? "none"} to {responsesAfter[status] ?? "none"}");
                }
            }
        }
    }

    private static void CompareSchemas(ApiContract before, ApiContract after, List<string> additive, List<string> breaking)
    {
        var old = before.Schemas.ToDictionary(s => s.WireName, StringComparer.Ordinal);
        var current = after.Schemas.ToDictionary(s => s.WireName, StringComparer.Ordinal);
        var sides = Sides(before);

        foreach (var name in current.Keys.Except(old.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            additive.Add($"new schema {name}");
        }

        foreach (var name in old.Keys.Except(current.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            breaking.Add($"schema {name} was removed");
        }

        foreach (var name in old.Keys.Intersect(current.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var side = sides.GetValueOrDefault(name);

            switch ((old[name], current[name]))
            {
                case (ObjectSchemaDef a, ObjectSchemaDef b):
                    CompareObject(name, side, a, b, additive, breaking);
                    break;

                case (UnionSchemaDef a, UnionSchemaDef b):
                    CompareUnion(name, side, a, b, additive, breaking);
                    break;

                default:
                    breaking.Add($"{name} changed shape between an object and a union");
                    break;
            }
        }
    }

    private static void CompareObject(string name, Side side, ObjectSchemaDef a, ObjectSchemaDef b, List<string> additive, List<string> breaking)
    {
        var old = a.Properties.ToDictionary(p => p.WireName, StringComparer.Ordinal);
        var current = b.Properties.ToDictionary(p => p.WireName, StringComparer.Ordinal);

        foreach (var property in current.Keys.Except(old.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var required = current[property].Required;

            // A new required input is one the SDK does not send; a new required output is one it
            // can ignore.
            (required && side.HasFlag(Side.Request) ? breaking : additive)
                .Add($"{name}.{property} was added ({(required ? "required" : "optional")}, {Describe(side)})");
        }

        foreach (var property in old.Keys.Except(current.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            breaking.Add($"{name}.{property} was removed ({Describe(side)})");
        }

        foreach (var property in old.Keys.Intersect(current.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var (x, y) = (old[property], current[property]);

            if (x.Required != y.Required)
            {
                // Tightening hurts whoever writes the value; loosening hurts whoever reads it.
                var hurts = y.Required ? side.HasFlag(Side.Request) : side.HasFlag(Side.Response);
                (hurts ? breaking : additive).Add($"{name}.{property} became {(y.Required ? "required" : "optional")} ({Describe(side)})");
            }

            if (x.Type != y.Type)
            {
                ClassifyTypeChange($"{name}.{property}", side, x.Type, y.Type, additive, breaking);
            }
        }
    }

    private static void ClassifyTypeChange(string where, Side side, TypeRef before, TypeRef after, List<string> additive, List<string> breaking)
    {
        if (before is JsonValueRef x && after is JsonValueRef y)
        {
            var added = y.Kinds & ~x.Kinds;
            var removed = x.Kinds & ~y.Kinds;

            // Kinds the service stops accepting break a sender; kinds it starts sending break a reader.
            if (removed != 0)
            {
                (side.HasFlag(Side.Request) ? breaking : additive).Add($"{where} no longer admits {removed} ({Describe(side)})");
            }

            if (added != 0)
            {
                (side.HasFlag(Side.Response) ? breaking : additive).Add($"{where} now admits {added} ({Describe(side)})");
            }

            return;
        }

        // A container keeps its shape and changes inside: judge the count and the element separately.
        if (before is ArrayRef ab && after is ArrayRef aa)
        {
            if (ab.MinItems != aa.MinItems)
            {
                ClassifyCount(where, "minItems", side, ab.MinItems, aa.MinItems, additive, breaking);
            }

            if (ab.Items != aa.Items)
            {
                ClassifyTypeChange(where + "[]", side, ab.Items, aa.Items, additive, breaking);
            }

            return;
        }

        if (before is MapRef mb && after is MapRef ma)
        {
            if (mb.MinProperties != ma.MinProperties)
            {
                ClassifyCount(where, "minProperties", side, mb.MinProperties, ma.MinProperties, additive, breaking);
            }

            if (mb.Value != ma.Value)
            {
                ClassifyTypeChange(where + "{}", side, mb.Value, ma.Value, additive, breaking);
            }

            return;
        }

        breaking.Add($"{where} changed type from {before} to {after}");
    }

    private static void ClassifyCount(string where, string keyword, Side side, int? before, int? after, List<string> additive, List<string> breaking)
    {
        var tighter = (after ?? 0) > (before ?? 0);
        (tighter && side.HasFlag(Side.Request) ? breaking : additive)
            .Add($"{where}: {keyword} moved from {before?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"} to {after?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"} ({Describe(side)})");
    }

    private static void CompareUnion(string name, Side side, UnionSchemaDef a, UnionSchemaDef b, List<string> additive, List<string> breaking)
    {
        if (a.DiscriminatorProperty != b.DiscriminatorProperty)
        {
            breaking.Add($"{name}: the discriminator moved from '{a.DiscriminatorProperty}' to '{b.DiscriminatorProperty}'");
        }

        var old = a.Members.ToDictionary(m => m.Tag, m => m.SchemaWireName, StringComparer.Ordinal);
        var current = b.Members.ToDictionary(m => m.Tag, m => m.SchemaWireName, StringComparer.Ordinal);

        foreach (var tag in current.Keys.Except(old.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            // An answer arrives with the type of the question that asked for it, so a new member
            // only appears on the wire once the SDK has a way to ask for it.
            additive.Add($"{name} gained the member '{tag}' ({Describe(side)})");
        }

        foreach (var tag in old.Keys.Except(current.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            breaking.Add($"{name} lost the member '{tag}' ({Describe(side)})");
        }
    }

    private static void CompareSemantics(SpecSnapshot candidate, List<string> breaking)
    {
        using var document = System.Text.Json.JsonDocument.Parse(candidate.CanonicalBytes);

        foreach (var conflict in candidate.Semantics.Conflicts)
        {
            foreach (var check in conflict.Checks.Where(c => !ContractValidator.Holds(document.RootElement, c)))
            {
                breaking.Add($"semantics.json '{conflict.Id}' relies on {check}, which the candidate no longer says");
            }
        }
    }

    [Flags]
    private enum Side
    {
        None = 0,
        Request = 1,
        Response = 2,
    }

    private static string Describe(Side side) => side switch
    {
        Side.Request => "sent",
        Side.Response => "received",
        Side.Request | Side.Response => "sent and received",
        _ => "unreached",
    };

    /// <summary>Which direction each schema travels in, from the operations that reach it.</summary>
    private static Dictionary<string, Side> Sides(ApiContract contract)
    {
        var sides = new Dictionary<string, Side>(StringComparer.Ordinal);

        void Walk(string name, Side side)
        {
            if (sides.TryGetValue(name, out var existing) && existing.HasFlag(side))
            {
                return;
            }

            sides[name] = existing | side;

            switch (contract.Schema(name))
            {
                case UnionSchemaDef union:
                    foreach (var member in union.Members)
                    {
                        Walk(member.SchemaWireName, side);
                    }

                    break;

                case ObjectSchemaDef obj:
                    foreach (var reference in obj.Properties.SelectMany(p => References(p.Type)))
                    {
                        Walk(reference, side);
                    }

                    break;
            }
        }

        foreach (var operation in contract.Operations)
        {
            if (operation.RequestSchema is { } request)
            {
                Walk(request, Side.Request);
            }

            foreach (var response in operation.Responses.Where(r => r.Schema is not null))
            {
                Walk(response.Schema!, Side.Response);
            }
        }

        return sides;
    }

    private static IEnumerable<string> References(TypeRef type) => type switch
    {
        SchemaRef s => [s.WireName],
        NullableRef n => [n.Inner.WireName],
        MapRef m => References(m.Value),
        ArrayRef a => References(a.Items),
        _ => [],
    };
}

/// <summary>The outcome of a diff, in the form <c>spec-check.yml</c> greps for.</summary>
/// <remarks>
/// The headings are the interface: <c>spec-check.yml</c> opens an issue when a line starts with one
/// of <c>Additive / safe (</c>, <c>Potentially breaking (</c>, <c>Annotations only (</c> or
/// <c>Other contract change (</c>. Changing their wording means changing the workflow too.
/// </remarks>
internal sealed record DiffReport(
    string CommittedUpstreamSha256,
    string CandidateUpstreamSha256,
    string CommittedSnapshotSha256,
    string CandidateSnapshotSha256,
    IReadOnlyList<string> Additive,
    IReadOnlyList<string> Breaking,
    IReadOnlyList<string> Notes)
{
    /// <summary>The candidate is byte for byte the document the snapshot was taken from.</summary>
    public bool Unchanged => CommittedUpstreamSha256 == CandidateUpstreamSha256;

    /// <summary>The document changed and the contract extracted from it did not.</summary>
    public bool AnnotationsOnly => !Unchanged && CommittedSnapshotSha256 == CandidateSnapshotSha256;

    /// <summary>The contract changed, and no operation or schema did: <c>info.version</c>, most likely.</summary>
    public bool Unclassified => !Unchanged && !AnnotationsOnly && Additive.Count == 0 && Breaking.Count == 0;

    public IEnumerable<string> Lines()
    {
        yield return $"upstream           : {CommittedUpstreamSha256} -> {CandidateUpstreamSha256}";
        yield return $"contract           : {CommittedSnapshotSha256} -> {CandidateSnapshotSha256}";

        if (Unchanged)
        {
            yield return "No change: the document is the one the snapshot was taken from.";
            yield break;
        }

        foreach (var note in Notes)
        {
            yield return $"note               : {note}";
        }

        if (AnnotationsOnly)
        {
            yield return "Annotations only (1):";
            yield return "  ~ the document changed and the contract did not: descriptions, titles, examples or layout. The generated code would not change.";
            yield break;
        }

        if (Unclassified)
        {
            yield return "Other contract change (1):";
            yield return "  ~ the contract changed, but no operation or schema did; the notes above say what moved.";
            yield break;
        }

        if (Additive.Count > 0)
        {
            yield return $"Additive / safe ({Additive.Count}):";

            foreach (var line in Additive)
            {
                yield return "  + " + line;
            }
        }

        if (Breaking.Count > 0)
        {
            yield return $"Potentially breaking ({Breaking.Count}):";

            foreach (var line in Breaking)
            {
                yield return "  ! " + line;
            }
        }
    }
}
