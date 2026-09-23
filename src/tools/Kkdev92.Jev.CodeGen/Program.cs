using System.CommandLine;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Kkdev92.Jev.CodeGen.Commands;
using Kkdev92.Jev.CodeGen.Specifications;

namespace Kkdev92.Jev.CodeGen;

/// <summary>
/// Entry point for the repository-internal contract generator.
/// </summary>
/// <remarks>
/// An explicit CLI, never a source generator, and never a network call during
/// <c>generate</c> or <c>verify</c>. A build does not change the contract; a reviewed commit does.
/// </remarks>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var contractOption = new Option<string>("--contract", "-c")
        {
            Description = "The contract to operate on, a directory under spec/.",
            DefaultValueFactory = _ => RepositoryLayout.DefaultContract,
        };

        var fetch = new Command("fetch", "Download TypeSafe's document, extract the contract into openapi.json and record both fingerprints in provenance.json. The document itself is not written. Uses the network unless --source-file is given.");
        var sourceFileOption = new Option<string?>("--source-file")
        {
            Description = "Import a document downloaded earlier instead of fetching one. Requires --retrieved-at.",
        };
        var retrievedAtOption = new Option<string?>("--retrieved-at")
        {
            Description = "UTC time the document was retrieved, for example 2026-09-22T21:13:07Z. Defaults to the server's Date header.",
        };
        fetch.Options.Add(contractOption);
        fetch.Options.Add(sourceFileOption);
        fetch.Options.Add(retrievedAtOption);
        fetch.SetAction((parseResult, cancellationToken) => GuardAsync(() => FetchCommand.RunAsync(
            parseResult.GetValue(contractOption)!,
            parseResult.GetValue(sourceFileOption),
            parseResult.GetValue(retrievedAtOption),
            cancellationToken)));

        var generate = new Command("generate", "Generate C# from the committed snapshot. Offline.");
        generate.Options.Add(contractOption);
        generate.SetAction(parseResult => Guard(() =>
            GenerateCommand.Run(parseResult.GetValue(contractOption)!, verifyOnly: false)));

        var verify = new Command("verify", "Fail if the checked-in generated sources are stale, missing or orphaned. Offline.");
        verify.Options.Add(contractOption);
        verify.SetAction(parseResult => Guard(() =>
            GenerateCommand.Run(parseResult.GetValue(contractOption)!, verifyOnly: true)));

        var diff = new Command("diff", "Classify the changes between the committed snapshot and another document. Reports; never writes.");
        var againstOption = new Option<string?>("--against")
        {
            Description = "Path to an OpenAPI document. When omitted, the live document is fetched.",
        };
        diff.Options.Add(contractOption);
        diff.Options.Add(againstOption);
        diff.SetAction((parseResult, cancellationToken) => GuardAsync(() => DiffCommand.RunAsync(
            parseResult.GetValue(contractOption)!,
            parseResult.GetValue(againstOption),
            cancellationToken)));

        // Not contract generation, but the same job: something the build produces that has to be
        // trimmed before it ships. It lives here rather than in a task assembly of its own because
        // this project is already the repository's build-time tool.
        var filterDocs = new Command("filter-docs", "Remove documentation for members outside the public surface. Offline.");
        var documentationOption = new Option<string>("--documentation") { Required = true };
        var assemblyOption = new Option<string>("--assembly") { Required = true };
        filterDocs.Options.Add(documentationOption);
        filterDocs.Options.Add(assemblyOption);
        filterDocs.SetAction(parseResult => Guard(() => FilterDocsCommand.Run(
            parseResult.GetValue(documentationOption)!,
            parseResult.GetValue(assemblyOption)!)));

        var root = new RootCommand("Deterministic contract generator for Kkdev92.Jev.")
        {
            fetch,
            generate,
            verify,
            diff,
            filterDocs,
        };

        return await root.Parse(args).InvokeAsync();
    }

    /// <summary>
    /// Converts an unexpected failure into a diagnostic and a non-zero exit code.
    /// </summary>
    /// <remarks>
    /// A stack trace is not useful output for a build step; the message is. The full exception is
    /// still printed when the tool is run with a debugger attached.
    /// </remarks>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Top-level CLI boundary: every failure becomes an exit code.")]
    private static int Guard(Func<int> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex)
        {
            Report(ex);
            return 1;
        }
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Top-level CLI boundary: every failure becomes an exit code.")]
    private static async Task<int> GuardAsync(Func<Task<int>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception ex)
        {
            Report(ex);
            return 1;
        }
    }

    private static void Report(Exception ex)
    {
        Console.Error.WriteLine($"codegen: {ex.Message}");

        if (Debugger.IsAttached)
        {
            Console.Error.WriteLine(ex);
        }
    }
}
