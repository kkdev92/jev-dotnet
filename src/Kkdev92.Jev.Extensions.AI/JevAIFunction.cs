using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.AI;

namespace Kkdev92.Jev.Extensions.AI;

/// <summary>Limits and request configuration for a decision function.</summary>
public sealed class JevFunctionOptions
{
    /// <summary>Per-call options copied at construction.</summary>
    public JevRequestOptions? RequestOptions { get; set; }
    /// <summary>Optional invocation deadline.</summary>
    public TimeSpan? TotalTimeout { get; set; }
    /// <summary>Clock for the invocation deadline.</summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;
    /// <summary>Maximum UTF-8 bytes of the input text.</summary>
    public int MaxInputUtf8Bytes { get; set; } = 1_048_576;
    /// <summary>Maximum bytes of the serialized result.</summary>
    public int MaxOutputUtf8Bytes { get; set; } = 1_048_576;
}

/// <summary>Create a typed, reflection-free decision function with a fixed input contract.</summary>
public static class JevAIFunction
{
    /// <summary>Expose a Jev projection as an AI function accepting one text argument named state.</summary>
    public static AIFunction Create<T>(JevClient client, JevProjection<T> projection, JsonTypeInfo<T> outputTypeInfo, JsonElement outputSchema,
        string name, string description, JevFunctionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentNullException.ThrowIfNull(outputTypeInfo);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(description);
        if (outputSchema.ValueKind != JsonValueKind.Object) { throw new ArgumentException("The output schema must be an object.", nameof(outputSchema)); }
        var o = options ?? new();
        ArgumentOutOfRangeException.ThrowIfLessThan(o.MaxInputUtf8Bytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(o.MaxOutputUtf8Bytes, 1);
        return new Function<T>(client, projection, outputTypeInfo, outputSchema.Clone(), name, description, Pipeline.Snapshot(o.RequestOptions),
            new PipelineSettings(o.TimeProvider, o.TotalTimeout), o.MaxInputUtf8Bytes, o.MaxOutputUtf8Bytes);
    }

    private static readonly JsonElement Input = JsonElement.Parse("""{"type":"object","properties":{"state":{"type":"string","minLength":1}},"required":["state"],"additionalProperties":false}""");

    private sealed class Function<T>(JevClient client, JevProjection<T> projection, JsonTypeInfo<T> output, JsonElement schema, string name,
        string description, JevRequestOptions request, PipelineSettings settings, int maxInput, int maxOutput) : AIFunction
    {
        public override string Name => name;
        public override string Description => description;
        public override JsonElement JsonSchema => Input;
        public override JsonElement? ReturnJsonSchema => schema;
        public override JsonSerializerOptions JsonSerializerOptions => FunctionJson.Default.Options;

        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            var run = new Pipeline(settings, cancellationToken) { Stage = JevAdapterStage.FunctionInvocation };
            bool failed = true;
            try
            {
                run.Check();
                if (arguments.Count != 1 || arguments.First().Key != "state") { throw Invalid(); }
                var argument = arguments.First().Value;
                string? text;
                try { text = argument is JsonElement { ValueKind: JsonValueKind.String } e ? e.GetString() : argument as string; }
                catch (Exception ex) when (Failures.Recoverable(ex)) { throw Invalid(); }
                if (string.IsNullOrWhiteSpace(text) || text.Length > maxInput || !JsonBudget.Valid(text) || Encoding.UTF8.GetByteCount(text) > maxInput)
                {
                    throw Invalid();
                }
                var result = await run.JudgeAsync(client, JevContent.FromText(text), projection.Plan, request, run.Stage).ConfigureAwait(false);
                var value = projection.Project(result);
                run.Check();
                var bytes = new JsonBudget(maxOutput, run.Stage, JevAdapterFailureReason.ResultLimitExceeded);
                try
                {
                    using var writer = new Utf8JsonWriter(bytes, JsonBudget.Format);
                    JsonSerializer.Serialize(writer, value, output);
                    await writer.FlushAsync(run.Token).ConfigureAwait(false);
                    var serialized = JsonElement.Parse(bytes.Written.Span);
                    failed = false;
                    return serialized;
                }
                catch (Exception ex) when (Failures.Recoverable(ex))
                {
                    throw Failures.From(ex, JevAdapterStage.ResultProjection, JevAdapterFailureReason.InvalidProjection, projection: true);
                }
            }
            finally { await run.FinishAsync(failed).ConfigureAwait(false); }
        }

        private static JevAdapterException Invalid() => new(JevAdapterStage.FunctionInvocation, JevAdapterFailureReason.InvalidFunctionArguments);
    }
}

[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class FunctionJson : JsonSerializerContext;
