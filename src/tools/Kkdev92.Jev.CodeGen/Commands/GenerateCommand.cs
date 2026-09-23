using Kkdev92.Jev.CodeGen.CSharp;
using Kkdev92.Jev.CodeGen.OpenApi;
using Kkdev92.Jev.CodeGen.Specifications;
using Kkdev92.Jev.CodeGen.Validation;

namespace Kkdev92.Jev.CodeGen.Commands;

/// <summary><c>generate</c> and <c>verify</c>: the offline half of the generator.</summary>
internal static class GenerateCommand
{
    /// <summary>The generated sources for a snapshot, keyed by path relative to the generated root.</summary>
    public static (ValidatedContract Contract, SortedDictionary<string, string> Files) Produce(RepositoryLayout layout, string contract)
    {
        var snapshot = SpecLoader.Load(layout, contract);
        var api = OpenApiParser.Parse(snapshot);
        var validated = ContractValidator.Validate(api, snapshot);

        return (validated, CSharpEmitter.Emit(validated));
    }

    public static int Run(string contract, bool verifyOnly) => Run(RepositoryLayout.Locate(), contract, verifyOnly, Console.Out);

    public static int Run(RepositoryLayout layout, string contract, bool verifyOnly, TextWriter output)
    {
        var (validated, files) = Produce(layout, contract);

        foreach (var warning in validated.Warnings)
        {
            output.WriteLine($"warning: {warning}");
        }

        var summary = $"{validated.Operations.Count} operations, {validated.ReachableSchemas.Count} schemas, {files.Count} files";

        if (verifyOnly)
        {
            var problems = Compare(layout.GeneratedRoot, files);

            foreach (var problem in problems)
            {
                output.WriteLine(problem);
            }

            if (problems.Count > 0)
            {
                output.WriteLine($"verify             : FAILED — {problems.Count} difference(s). Run 'codegen generate' and review the diff.");
                return 1;
            }

            output.WriteLine($"verify             : ok ({summary}; spec {validated.Contract.SnapshotSha256[..12]})");
            return 0;
        }

        Replace(layout.GeneratedRoot, files);
        output.WriteLine($"generate           : wrote {summary} to {Path.GetRelativePath(layout.Root, layout.GeneratedRoot)}");
        return 0;
    }

    /// <summary>Every way the files on disk can differ from what the snapshot produces.</summary>
    internal static List<string> Compare(string generatedRoot, IReadOnlyDictionary<string, string> expected)
    {
        var problems = new List<string>();

        foreach (var (relative, content) in expected)
        {
            var path = Path.Combine(generatedRoot, relative.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(path))
            {
                problems.Add($"missing            : {relative}");
                continue;
            }

            if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(JsonCanonicalizer.Encoding.GetBytes(content)))
            {
                problems.Add($"stale              : {relative}");
            }
        }

        if (Directory.Exists(generatedRoot))
        {
            foreach (var path in Directory.EnumerateFiles(generatedRoot, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(generatedRoot, path).Replace(Path.DirectorySeparatorChar, '/');

                if (!expected.ContainsKey(relative))
                {
                    // A file for a schema that no longer exists compiles, and would keep compiling.
                    problems.Add($"orphan             : {relative}");
                }
            }
        }

        return problems.Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Writes the full set to a fresh directory and only then swaps it in, so a failure part-way
    /// through never leaves half a generation behind.
    /// </summary>
    private static void Replace(string generatedRoot, IReadOnlyDictionary<string, string> files)
    {
        var parent = Path.GetDirectoryName(generatedRoot)!;
        var staging = Path.Combine(parent, "Generated.staging");
        var retired = Path.Combine(parent, "Generated.retired");

        foreach (var leftover in new[] { staging, retired })
        {
            if (Directory.Exists(leftover))
            {
                Directory.Delete(leftover, recursive: true);
            }
        }

        foreach (var (relative, content) in files)
        {
            var path = Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, JsonCanonicalizer.Encoding.GetBytes(content));
        }

        var problems = Compare(staging, files);

        if (problems.Count > 0)
        {
            throw new InvalidOperationException($"The staged output does not read back as written: {string.Join("; ", problems)}");
        }

        if (Directory.Exists(generatedRoot))
        {
            Directory.Move(generatedRoot, retired);
        }

        Directory.Move(staging, generatedRoot);

        if (Directory.Exists(retired))
        {
            Directory.Delete(retired, recursive: true);
        }
    }
}
