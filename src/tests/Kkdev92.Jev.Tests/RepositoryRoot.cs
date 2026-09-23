namespace Kkdev92.Jev.Tests;

/// <summary>Locates the repository root from the test output directory.</summary>
/// <remarks>The root is the directory that holds <c>src/Jev.slnx</c>; the tests themselves live in <c>src/tests</c>.</remarks>
internal static class RepositoryRoot
{
    private const string SolutionRelativePath = "src/Jev.slnx";

    /// <summary>The absolute path of the repository root.</summary>
    public static string Value { get; } = Locate();

    private static string Locate()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, SolutionRelativePath)))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException($"Could not locate {SolutionRelativePath} above '{AppContext.BaseDirectory}'.");
    }
}
