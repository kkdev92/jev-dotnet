using System.Diagnostics;
using System.Xml.Linq;

namespace Kkdev92.Jev.Tests;

/// <summary>
/// The package-validation exemption covers the first release and cannot quietly outlive it.
/// </summary>
/// <remarks>
/// <para>
/// <c>PackageValidationBaselineVersion</c> is what compares the assemblies being packed against
/// the last version on nuget.org, so a binary breaking change fails the pack rather than reaching
/// a consumer. 0.1.0-alpha was the first release and had nothing to compare against;
/// <c>LastVersionWithoutBaseline</c> records the one version that absence was decided for, and every
/// release since names the one before it as the baseline.
/// </para>
/// <para>
/// The risk is not the decision, it is inheriting it. A note in a comment saying "set this after
/// the first publish" is exactly the kind of instruction that is still there three releases later,
/// with nothing having compared anything in the meantime — so the build refuses to go past the
/// exempted version without a baseline, and these tests prove the refusal is real.
/// </para>
/// </remarks>
public sealed class PackageValidationBaselineTests
{
    private static string PropsPath => Path.Combine(RepositoryRoot.Value, "src", "Package.props");

    private static string? Property(string name)
        => XDocument.Load(PropsPath).Descendants(name).FirstOrDefault()?.Value;

    [Fact]
    public void TheVersionIsNotBehindTheExemption()
    {
        var exemption = Property("LastVersionWithoutBaseline");
        var version = Property("VersionPrefix");

        Assert.NotNull(exemption);
        Assert.NotNull(version);
        Assert.True(
            Version.Parse(version) >= Version.Parse(exemption),
            $"VersionPrefix is {version} and the baseline exemption names {exemption}. A version "
            + "below the exemption was never released under it and cannot be built now.");
    }

    [Fact]
    public void PastTheExemptionTheBaselineIsSet()
    {
        var exemption = Version.Parse(Property("LastVersionWithoutBaseline")!);
        var version = Version.Parse(Property("VersionPrefix")!);

        if (version > exemption)
        {
            Assert.False(
                string.IsNullOrWhiteSpace(Property("PackageValidationBaselineVersion")),
                $"VersionPrefix {version} is past the exemption for {exemption}, and nothing is "
                + "comparing binary compatibility with the release before it.");
        }

        // Everything package validation does that is not a comparison — framework compatibility,
        // package structure — runs either way, so it stays on even while there is no baseline.
        Assert.Equal("true", Property("EnablePackageValidation"));
    }

    /// <summary>
    /// The guard actually stops a build, rather than being a property nobody reads.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Run as a build rather than asserted from the file, because what matters is the behaviour:
    /// an MSBuild condition can be written so it never evaluates true, and reading it would not
    /// say so. An empty command-line property clears any baseline the file sets — a global
    /// property wins — and the guard tests for emptiness rather than for absence, which is the
    /// state a release that deleted the line would be in.
    /// </para>
    /// <para>
    /// The accepting direction is <see cref="PastTheExemptionTheGuardAdmitsABaseline"/>.
    /// </para>
    /// <para>
    /// <c>--no-restore</c>, so the probe cannot rewrite the assets file the real build uses, and a
    /// separate output path so it cannot disturb the binaries the rest of the suite reads. The
    /// guard runs before compilation, so nothing is compiled either.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task PastTheExemptionTheBuildStopsUntilABaselineIsSet()
    {
        var project = Path.Combine(RepositoryRoot.Value, "src", "Kkdev92.Jev", "Kkdev92.Jev.csproj");

        var (exitCode, output) = await BuildAsync(project, "-p:VersionPrefix=99.0.0", "-p:PackageValidationBaselineVersion=");

        Assert.NotEqual(0, exitCode);
        Assert.Contains("no PackageValidationBaselineVersion is set", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// With a baseline set, a version past the exemption goes through the guard.
    /// </summary>
    /// <remarks>
    /// Only the guard's target runs, not a build. A build would restore the baseline package from
    /// nuget.org and compile into the intermediate folder the real build uses, and neither says
    /// anything about the guard: its condition is what decides. Run for a version past the
    /// exemption with the baseline <c>src/Package.props</c> sets, the target has to be skipped and
    /// the run has to end cleanly — a guard that refused every version past the exemption, baseline
    /// or not, would fail here and nowhere else.
    /// </remarks>
    [Fact]
    public async Task PastTheExemptionTheGuardAdmitsABaseline()
    {
        var project = Path.Combine(RepositoryRoot.Value, "src", "Kkdev92.Jev", "Kkdev92.Jev.csproj");

        var (exitCode, output) = await DotnetAsync(
            "msbuild", project, "-t:RequireAPackageValidationBaseline", "-p:VersionPrefix=99.0.0");

        Assert.True(exitCode == 0, output);
        Assert.DoesNotContain("no PackageValidationBaselineVersion is set", output, StringComparison.Ordinal);
    }

    private static Task<(int ExitCode, string Output)> BuildAsync(string project, params string[] arguments)
        => DotnetAsync(
            "build",
            project,
            [
                "--no-restore",
                "-p:BaseOutputPath=" + Path.Combine(Path.GetTempPath(), "jev-baseline-probe") + Path.DirectorySeparatorChar,
                .. arguments,
            ]);

    private static async Task<(int ExitCode, string Output)> DotnetAsync(string command, string project, params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = RepositoryRoot.Value,
        };

        start.ArgumentList.Add(command);
        start.ArgumentList.Add(project);

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        var cancellationToken = TestContext.Current.CancellationToken;

        using var process = Process.Start(start)!;

        // Both streams at once: reading one to the end while the other fills its pipe buffer is
        // how a child process deadlocks against its parent.
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);

        return (process.ExitCode, await standardOutput + await standardError);
    }
}
