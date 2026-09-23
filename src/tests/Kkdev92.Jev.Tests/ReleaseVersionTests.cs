using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Kkdev92.Jev.Tests;

/// <summary>
/// The version being built, the newest changelog entry, the readme's status line and the
/// package-validation baseline agree.
/// </summary>
/// <remarks>
/// <para>
/// Four statements about the version are kept by hand in four places, and each is only correct
/// relative to the others. <c>VersionPrefix</c> says what is being built, the changelog says what
/// shipped, the readme's status line says which release its claims are about, and
/// <c>PackageValidationBaselineVersion</c> says what the assemblies are compared against — which
/// has to be the release immediately before this one, or the comparison is answering a question
/// nobody asked.
/// </para>
/// <para>
/// <c>VersionPrefix</c> is bumped in the release commit rather than ahead of one. So once
/// something has shipped, the newest dated changelog entry is the version being built; before the
/// first release there is no dated entry at all, the work is under <c>[Unreleased]</c>, and the
/// only version that can be being built is the first one.
/// </para>
/// <para>
/// Read from the changelog rather than from nuget.org on purpose. The authoritative answer is what
/// is published, but a test that asks the network is a test that fails on a train, and the restore
/// already refuses a baseline that is not published — <c>NU1102</c>, at pack time, loudly. What is
/// left uncovered is the baseline that exists but is not the latest, and the changelog knows that
/// offline.
/// </para>
/// </remarks>
public sealed partial class ReleaseVersionTests
{
    private static string PropsPath => Path.Combine(RepositoryRoot.Value, "src", "Package.props");

    private static string? Property(string name)
        => XDocument.Load(PropsPath).Descendants(name).FirstOrDefault()?.Value;

    /// <summary>The version this build produces, spelled the way a package file name spells it.</summary>
    private static string BuiltVersion
    {
        get
        {
            var prefix = Property("VersionPrefix");
            var suffix = Property("VersionSuffix");

            Assert.False(string.IsNullOrWhiteSpace(prefix), "VersionPrefix is not set.");

            return string.IsNullOrWhiteSpace(suffix) ? prefix! : $"{prefix}-{suffix}";
        }
    }

    private static string Changelog => File.ReadAllText(Path.Combine(RepositoryRoot.Value, "CHANGELOG.md"));

    /// <summary>
    /// The released versions the changelog names, newest first.
    /// </summary>
    /// <remarks>
    /// <c>[Unreleased]</c> is skipped: it is a heading for work that has not shipped, and treating
    /// it as a release would make the newest entry disagree with everything on the first commit
    /// after a release.
    /// </remarks>
    private static IReadOnlyList<(string Version, string Date)> Released()
        =>
        [
            .. ReleaseHeading().Matches(Changelog)
                .Select(match => (match.Groups["version"].Value, match.Groups["date"].Value))
                .Where(entry => !entry.Item1.Equals("Unreleased", StringComparison.OrdinalIgnoreCase))
        ];

    [GeneratedRegex(@"^## \[(?<version>[^\]]+)\](?: - (?<date>\S+))?", RegexOptions.Multiline)]
    private static partial Regex ReleaseHeading();

    [Fact]
    public void TheChangelogsNewestEntryIsTheVersionBeingBuilt()
    {
        var released = Released();

        if (released.Count == 0)
        {
            // Nothing has shipped. Then the version being built is the first one — the one the
            // baseline exemption was written for — and what it will contain is under Unreleased.
            Assert.Equal(Property("LastVersionWithoutBaseline"), Property("VersionPrefix"));
            Assert.Matches(UnreleasedHeading(), Changelog);
            return;
        }

        Assert.Equal(BuiltVersion, released[0].Version);
    }

    [GeneratedRegex(@"^## \[Unreleased\]\s*$", RegexOptions.Multiline)]
    private static partial Regex UnreleasedHeading();

    /// <summary>
    /// The newest entry carries a date, because the version it names has shipped.
    /// </summary>
    /// <remarks>
    /// There is no window in which the newest entry is legitimately undated: the version it matches
    /// is always a release that went out. Work in progress belongs under <c>[Unreleased]</c>, which
    /// this does not look at — and a version heading that says "unreleased" is exactly the stale
    /// line this is here to refuse.
    /// </remarks>
    [Fact]
    public void EveryReleasedEntryIsDated()
    {
        foreach (var (version, date) in Released())
        {
            Assert.True(
                DateOnly.TryParseExact(date, "yyyy-MM-dd", out _),
                $"The changelog entry for {version} reads '{date}'. A version heading is written when "
                + "the version ships, so the date it shipped is what belongs there — UTC, as the "
                + "preamble says.");
        }
    }

    /// <summary>
    /// The readme's status line names the version being built.
    /// </summary>
    /// <remarks>
    /// It is not decoration. What follows it is a claim about that release and nothing else — what
    /// has been verified against the live service and what has not — so a stale number does not
    /// read as an out-of-date label, it reads as those claims being about a version they were
    /// never true of.
    /// </remarks>
    [Fact]
    public void TheReadmeStatusLineNamesTheVersionBeingBuilt()
    {
        var readme = File.ReadAllText(Path.Combine(RepositoryRoot.Value, "README.md"));
        var status = StatusLine().Match(readme);

        Assert.True(status.Success, "The readme has no `**Status:** `x.y.z`` line to check.");
        Assert.Equal(BuiltVersion, status.Groups["version"].Value);
    }

    [GeneratedRegex(@"\*\*Status:\*\* `(?<version>[^`]+)`")]
    private static partial Regex StatusLine();

    /// <summary>
    /// The baseline names the release immediately before the one being built, and there is none
    /// until there is such a release.
    /// </summary>
    /// <remarks>
    /// This is the easiest of the four to forget. Bumping the version and writing the changelog
    /// entry are what a release feels like; moving the baseline is a third edit in a third file
    /// that changes nothing anybody can see, and leaving it behind costs nothing until the release
    /// after that.
    /// </remarks>
    [Fact]
    public void TheBaselineNamesThePreviousRelease()
    {
        var released = Released();
        var baseline = Property("PackageValidationBaselineVersion");

        if (released.Count < 2)
        {
            // Nothing before the version being built has shipped, so there is nothing to compare
            // against; a baseline here could only name a version that does not exist.
            Assert.True(string.IsNullOrWhiteSpace(baseline), $"The baseline names {baseline}, but no earlier release is in the changelog.");
            return;
        }

        Assert.Equal(released[1].Version, baseline);
    }
}
