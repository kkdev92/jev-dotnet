namespace Kkdev92.Jev.CodeGen.Specifications;

/// <summary>Where the specification inputs and the generated outputs live in this repository.</summary>
internal sealed class RepositoryLayout
{
    /// <summary>The solution, relative to the repository root. Its presence is what identifies the root.</summary>
    public const string SolutionRelativePath = "src/Jev.slnx";

    /// <summary>The contract this repository currently generates from.</summary>
    public const string DefaultContract = "typesafe-v1";

    private RepositoryLayout(string root) => Root = root;

    /// <summary>The absolute path of the repository root.</summary>
    public string Root { get; }

    /// <summary><c>spec/</c>.</summary>
    public string SpecRoot => Path.Combine(Root, "spec");

    /// <summary>The generated sources of the core package, relative to which every emitted path is written.</summary>
    public string GeneratedRoot => Path.Combine(Root, "src", "Kkdev92.Jev", "Generated");

    /// <summary>The directory holding one contract's snapshot and companion files.</summary>
    public string ContractDirectory(string contract)
    {
        // A contract name becomes a path segment, so it is held to the shape the directory uses.
        if (string.IsNullOrEmpty(contract)
            || contract.Any(c => !(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-')))
        {
            throw new ArgumentException($"'{contract}' is not a contract name. Expected lower-case letters, digits and '-'.", nameof(contract));
        }

        return Path.Combine(SpecRoot, contract);
    }

    /// <summary>Finds the repository root above the current directory, or above the tool's own location.</summary>
    public static RepositoryLayout Locate()
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);

            while (directory is not null)
            {
                if (IsRoot(directory.FullName))
                {
                    return new RepositoryLayout(directory.FullName);
                }

                directory = directory.Parent;
            }
        }

        throw new InvalidOperationException($"Could not find {SolutionRelativePath} in or above '{Environment.CurrentDirectory}'. Run the generator from inside the repository.");
    }

    /// <summary>True when the directory is the repository root: the one holding <see cref="SolutionRelativePath"/>.</summary>
    public static bool IsRoot(string directory) => File.Exists(Path.Combine(directory, SolutionRelativePath));

    /// <summary>A layout rooted at an explicit directory, for tests that build a repository in a temporary folder.</summary>
    public static RepositoryLayout At(string root) => new(Path.GetFullPath(root));
}
