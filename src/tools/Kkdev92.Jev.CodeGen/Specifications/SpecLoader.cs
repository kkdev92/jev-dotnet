namespace Kkdev92.Jev.CodeGen.Specifications;

/// <summary>Everything the generator reads for one contract, already checked against its provenance.</summary>
/// <param name="CanonicalBytes">The contract: <c>openapi.json</c>, extracted and canonical.</param>
internal sealed record SpecSnapshot(
    string Contract,
    byte[] CanonicalBytes,
    Provenance Provenance,
    PublicSurface PublicSurface,
    Semantics Semantics,
    NamingOverrides NamingOverrides);

/// <summary>Loads a committed snapshot and refuses to go further if the bytes are not the recorded ones.</summary>
internal static class SpecLoader
{
    public static SpecSnapshot Load(RepositoryLayout layout, string contract)
    {
        var directory = layout.ContractDirectory(contract);

        if (!Directory.Exists(directory))
        {
            throw new InvalidOperationException($"No specification at '{directory}'.");
        }

        // A copy of TypeSafe's own document beside the contract is refused rather than read past.
        // Git ignores it, but only until somebody forces it in, and nothing under spec/ is meant to
        // need a second look before it is committed. Keep copies outside the repository.
        foreach (var copy in Provenance.UpstreamCopyNames.Where(name => File.Exists(Path.Combine(directory, name))))
        {
            throw new InvalidDataException(
                $"{copy} looks like TypeSafe's document, which this repository does not keep: TypeSafe's terms of use do not "
                + "permit redistributing it. The contract in openapi.json is extracted from it by 'codegen fetch'. Move the copy out of spec/.");
        }

        var provenance = Provenance.Read(Path.Combine(directory, Provenance.FileName));
        var canonical = File.ReadAllBytes(Path.Combine(directory, Provenance.SnapshotFileName));

        Verify(contract, provenance, canonical);

        return new SpecSnapshot(
            contract,
            canonical,
            provenance,
            PublicSurface.Read(Path.Combine(directory, PublicSurface.FileName)),
            Semantics.Read(Path.Combine(directory, Semantics.FileName)),
            NamingOverrides.Read(Path.Combine(directory, NamingOverrides.FileName)));
    }

    /// <summary>
    /// The committed contract is the one the provenance records, in the form the tool writes.
    /// </summary>
    /// <remarks>
    /// A mismatch here almost always means git rewrote a file — <c>core.autocrlf</c> on a Windows
    /// checkout — or somebody edited the snapshot by hand. Neither is repaired automatically: the
    /// message says which check failed, and <c>git checkout -- spec</c> or <c>codegen fetch</c> is the
    /// human's choice to make.
    /// </remarks>
    internal static void Verify(string contract, Provenance provenance, ReadOnlySpan<byte> canonical)
    {
        if (!string.Equals(provenance.Contract, contract, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{Provenance.FileName} describes '{provenance.Contract}', not '{contract}'.");
        }

        var fingerprint = FileFingerprint.Of(Provenance.SnapshotFileName, canonical);

        if (fingerprint != provenance.Snapshot)
        {
            throw new InvalidDataException(
                $"{Provenance.SnapshotFileName} is not the recorded snapshot: SHA-256 {fingerprint.Sha256} ({fingerprint.ByteLength} bytes), "
                + $"expected {provenance.Snapshot.Sha256} ({provenance.Snapshot.ByteLength} bytes). Restore it with 'git checkout -- spec', or refresh it with 'codegen fetch'.");
        }

        if (!ContractExtractor.IsContract(canonical))
        {
            throw new InvalidDataException(
                $"{Provenance.SnapshotFileName} is not an extracted, canonical contract: it carries annotations or is not in canonical form. "
                + "It must never be edited by hand; restore it with 'git checkout -- spec', or regenerate it with 'codegen fetch'.");
        }
    }
}
