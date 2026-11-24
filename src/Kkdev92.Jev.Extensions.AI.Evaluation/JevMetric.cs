using Microsoft.Extensions.AI.Evaluation;

namespace Kkdev92.Jev.Extensions.AI.Evaluation;

/// <summary>A named metric selector registered with a Jev evaluator.</summary>
public abstract class JevMetricDefinition<T>
{
    internal JevMetricDefinition(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Any(char.IsControl)) { throw new ArgumentException("A metric name contains a control character.", nameof(name)); }
        Name = name;
    }
    /// <summary>The stable metric name supplied by the host.</summary>
    public string Name { get; }
    internal abstract EvaluationMetric Create(T input);
    internal abstract EvaluationMetric Unavailable();
}

/// <summary>Construct named, typed evaluation metrics.</summary>
public static class JevMetric
{
    /// <summary>Project a finite numeric metric.</summary>
    public static JevMetricDefinition<T> Numeric<T>(string name, Func<T, double> value, Func<T, EvaluationMetricInterpretation>? interpretation = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new Definition<T>(name, x =>
        {
            var number = value(x);
            return double.IsFinite(number) ? new NumericMetric(name, number) : throw Invalid();
        }, () => new NumericMetric(name), interpretation);
    }
    /// <summary>Project a Boolean metric.</summary>
    public static JevMetricDefinition<T> Boolean<T>(string name, Func<T, bool> value, Func<T, EvaluationMetricInterpretation>? interpretation = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new Definition<T>(name, x => new BooleanMetric(name, value(x)), () => new BooleanMetric(name), interpretation);
    }
    /// <summary>Project a string metric.</summary>
    public static JevMetricDefinition<T> String<T>(string name, Func<T, string> value, Func<T, EvaluationMetricInterpretation>? interpretation = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new Definition<T>(name, x => new StringMetric(name, value(x) ?? throw Invalid()), () => new StringMetric(name), interpretation);
    }

    private static JevProjectionException Invalid() => new(JevAdapterStage.ResultProjection, JevAdapterFailureReason.InvalidProjection);

    private sealed class Definition<T>(string name, Func<T, EvaluationMetric> create, Func<EvaluationMetric> empty,
        Func<T, EvaluationMetricInterpretation>? interpret) : JevMetricDefinition<T>(name)
    {
        internal override EvaluationMetric Create(T input)
        {
            var metric = create(input);
            var interpretation = interpret is null ? new EvaluationMetricInterpretation() : interpret(input);
            if (interpretation is null || !Enum.IsDefined(interpretation.Rating)) { throw Invalid(); }
            metric.Interpretation = new(interpretation.Rating, interpretation.Failed);
            return metric;
        }
        internal override EvaluationMetric Unavailable()
        {
            var metric = empty();
            metric.Interpretation = new(failed: true);
            metric.Diagnostics = [EvaluationDiagnostic.Error("JEV_EVALUATION_UNAVAILABLE")];
            return metric;
        }
    }
}
