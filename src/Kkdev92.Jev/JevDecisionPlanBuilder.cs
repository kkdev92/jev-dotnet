using System.Buffers;
using System.Globalization;
using System.Text.Json;
using Kkdev92.Jev.Internal;
using Kkdev92.Jev.Planning;
using Kkdev92.Jev.Serialization;
using Kkdev92.Jev.Wire;

namespace Kkdev92.Jev;

/// <summary>
/// Registers questions and builds a <see cref="JevDecisionPlan"/> from them, once.
/// </summary>
/// <remarks>
/// <para>
/// Each <c>Add</c> method validates its question completely before registering it, copies what it
/// was given, and returns a typed handle for reading the answer. A question that fails validation
/// is not registered, and the builder stays usable. <see cref="Build"/> can be called once; after
/// it, successful or not, the builder accepts nothing more.
/// </para>
/// <para>
/// Not thread-safe: build a plan on one thread, then share the plan.
/// </para>
/// <para>
/// Ids, labels and every piece of text are compared and sent exactly as given — ordinal, never
/// trimmed, normalised or re-cased. Two labels that differ only in case are two labels.
/// </para>
/// </remarks>
public sealed class JevDecisionPlanBuilder
{
    /// <summary>The most questions one plan may hold. A local limit of this SDK, not one TypeSafe publishes.</summary>
    public const int MaxQuestions = 1024;

    /// <summary>The largest encoded <c>questions</c> object one plan may produce, in bytes. A local limit.</summary>
    public const int MaxEncodedQuestionsBytes = 1024 * 1024;

    /// <summary>The most options a choice question may have, as TypeSafe documents.</summary>
    public const int MaxChoiceOptions = JevContract.ChoiceOptionsMaximum;

    /// <summary>The fewest options a choice question may have.</summary>
    public const int MinChoiceOptions = JevContract.ChoiceOptionsMinimum;

    /// <summary>The most levels a score question may have, as TypeSafe documents.</summary>
    public const int MaxScoreLevels = JevContract.ScoreLevelsMaximum;

    /// <summary>
    /// The fewest levels a score question may have. The OpenAPI document allows one; a one-level
    /// score can only ever answer 0, so this SDK requires two, as TypeSafe's reference recommends.
    /// </summary>
    public const int MinScoreLevels = JevContract.ScoreLevelsMinimum;

    private readonly PlanToken _token = new();
    private readonly List<QuestionDefinition> _questions = [];
    private readonly HashSet<string> _ids = new(StringComparer.Ordinal);
    private State _state;

    private enum State
    {
        Mutable,
        Frozen,
        Faulted,
    }

    /// <summary>How many questions have been registered.</summary>
    public int Count => _questions.Count;

    /// <summary>Adds a question that selects one option from a set.</summary>
    /// <typeparam name="T">The value each option maps to. Any type; not only an enum.</typeparam>
    /// <param name="id">
    /// The name the answer comes back under. Never sent to the model and never used in inference;
    /// it only matches the answer to the question. Unique within the plan.
    /// </param>
    /// <param name="instructions">
    /// What the model should decide: text, an object or an array. <c>default</c> leaves it out;
    /// <see cref="JevContent.Null"/> sends an explicit <c>null</c>.
    /// </param>
    /// <param name="options">
    /// Between <see cref="MinChoiceOptions"/> and <see cref="MaxChoiceOptions"/> options, with unique
    /// labels. Copied: changing the caller's array afterwards changes nothing.
    /// </param>
    /// <returns>A handle for reading the answer from a <see cref="JevResult"/> of this plan.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="id"/>, a label or a value is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An argument breaks a rule above. The message never quotes ids, labels or content.</exception>
    /// <exception cref="InvalidOperationException">The builder has already built, or already holds <see cref="MaxQuestions"/> questions.</exception>
    public JevChoiceHandle<T> AddChoice<T>(string id, JevContent instructions, ReadOnlySpan<JevChoiceOption<T>> options)
        where T : notnull
    {
        ThrowIfNotMutable();
        ValidateId(id);
        ValidateInstructions(instructions);

        if (options.Length is < MinChoiceOptions or > MaxChoiceOptions)
        {
            throw new ArgumentException(
                $"A choice question needs between {MinChoiceOptions.ToString(CultureInfo.InvariantCulture)} and {MaxChoiceOptions.ToString(CultureInfo.InvariantCulture)} options; {options.Length.ToString(CultureInfo.InvariantCulture)} were given.",
                nameof(options));
        }

        var values = new T[options.Length];
        var labels = new string[options.Length];
        var descriptions = new JevContent[options.Length];
        var index = new Dictionary<string, int>(options.Length, StringComparer.Ordinal);

        for (var i = 0; i < options.Length; i++)
        {
            var option = options[i];

            if (option.Value is null)
            {
                throw new ArgumentNullException(nameof(options), $"The value of option {i.ToString(CultureInfo.InvariantCulture)} is null.");
            }

            if (option.Label is null)
            {
                throw new ArgumentNullException(nameof(options), $"The label of option {i.ToString(CultureInfo.InvariantCulture)} is null.");
            }

            if (option.Label.Length == 0)
            {
                throw new ArgumentException($"The label of option {i.ToString(CultureInfo.InvariantCulture)} is empty. The model reads labels; an empty one says nothing.", nameof(options));
            }

            TextRules.ThrowIfMalformed(option.Label, nameof(options), $"label of option {i.ToString(CultureInfo.InvariantCulture)}");

            if (!index.TryAdd(option.Label, i))
            {
                throw new ArgumentException($"Option {i.ToString(CultureInfo.InvariantCulture)} repeats a label. Labels are compared ordinally and must be unique.", nameof(options));
            }

            ValidateContent(option.Description, nameof(options), $"description of option {i.ToString(CultureInfo.InvariantCulture)}", allowNull: true);

            values[i] = option.Value;
            labels[i] = option.Label;
            descriptions[i] = option.Description;
        }

        var definition = new ChoiceQuestionDefinition<T>(_token, _questions.Count, id, instructions, values, labels, descriptions, index);
        Register(definition);
        return new JevChoiceHandle<T>(definition);
    }

    /// <summary>Adds a question that rates the state against ordered levels.</summary>
    /// <param name="id">The name the answer comes back under. See <see cref="AddChoice{T}"/>.</param>
    /// <param name="instructions">What the model should rate. See <see cref="AddChoice{T}"/>.</param>
    /// <param name="levels">
    /// Between <see cref="MinScoreLevels"/> and <see cref="MaxScoreLevels"/> level descriptions, from
    /// the low end to the high end: text, objects or arrays. A level's score is its position,
    /// starting at 0. <see langword="null"/> and <c>default</c> are refused, as the contract
    /// requires. Copied.
    /// </param>
    /// <returns>A handle for reading the answer from a <see cref="JevResult"/> of this plan.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="id"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An argument breaks a rule above.</exception>
    /// <exception cref="InvalidOperationException">The builder has already built, or is full.</exception>
    public JevScoreHandle AddScore(string id, JevContent instructions, ReadOnlySpan<JevContent> levels)
    {
        ThrowIfNotMutable();
        ValidateId(id);
        ValidateInstructions(instructions);

        if (levels.Length is < MinScoreLevels or > MaxScoreLevels)
        {
            throw new ArgumentException(
                $"A score question needs between {MinScoreLevels.ToString(CultureInfo.InvariantCulture)} and {MaxScoreLevels.ToString(CultureInfo.InvariantCulture)} levels; {levels.Length.ToString(CultureInfo.InvariantCulture)} were given.",
                nameof(levels));
        }

        for (var i = 0; i < levels.Length; i++)
        {
            ValidateContent(levels[i], nameof(levels), $"level {i.ToString(CultureInfo.InvariantCulture)}", allowNull: false);
        }

        var definition = new ScoreQuestionDefinition(_token, _questions.Count, id, instructions, levels.ToArray());
        Register(definition);
        return new JevScoreHandle(definition);
    }

    /// <summary>Adds a yes/no question, answered with the probability of yes.</summary>
    /// <param name="id">The name the answer comes back under. See <see cref="AddChoice{T}"/>.</param>
    /// <param name="instructions">The question or statement to judge. See <see cref="AddChoice{T}"/>.</param>
    /// <param name="criteria">What counts as yes and as no. <c>default</c> sends none.</param>
    /// <returns>A handle for reading the answer from a <see cref="JevResult"/> of this plan.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="id"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An argument breaks a rule above.</exception>
    /// <exception cref="InvalidOperationException">The builder has already built, or is full.</exception>
    public JevNoulHandle AddNoul(string id, JevContent instructions, JevNoulCriteria criteria = default)
    {
        ThrowIfNotMutable();
        ValidateId(id);
        ValidateInstructions(instructions);

        if (!criteria.True.IsUnspecified)
        {
            ValidateContent(criteria.True, nameof(criteria), "true criterion", allowNull: true);
        }

        if (!criteria.False.IsUnspecified)
        {
            ValidateContent(criteria.False, nameof(criteria), "false criterion", allowNull: true);
        }

        var definition = new NoulQuestionDefinition(_token, _questions.Count, id, instructions, criteria);
        Register(definition);
        return new JevNoulHandle(definition);
    }

    /// <summary>Validates the whole plan, encodes its questions once, and freezes it.</summary>
    /// <returns>The plan. Every handle this builder issued belongs to it.</returns>
    /// <exception cref="InvalidOperationException">
    /// The builder has already built, has no questions, or its questions encode to more than
    /// <see cref="MaxEncodedQuestionsBytes"/>. After a failed build the builder cannot be used again.
    /// </exception>
    public JevDecisionPlan Build()
    {
        ThrowIfNotMutable();

        // Faulted until proven otherwise: whatever fails below, this builder is not used again.
        _state = State.Faulted;

        if (_questions.Count == 0)
        {
            throw new InvalidOperationException("A plan needs at least one question: the API refuses an empty 'questions' object.");
        }

        var questions = _questions.ToArray();
        var encoded = Encode(questions);
        var plan = new JevDecisionPlan(_token, questions, encoded);

        _state = State.Frozen;
        return plan;
    }

    /// <summary>
    /// Writes the <c>questions</c> object once. Every call copies these bytes into its request as
    /// they are, so they are produced here, by the SDK, from content it validated — which is what
    /// makes skipping the writer's validation at send time sound.
    /// </summary>
    private static byte[] Encode(QuestionDefinition[] questions)
    {
        var buffer = new ArrayBufferWriter<byte>(4096);

        using (var writer = new Utf8JsonWriter(buffer, WireEncoding.WriterOptions))
        {
            writer.WriteStartObject();

            foreach (var question in questions)
            {
                writer.WritePropertyName(question.Id);
                question.WriteTo(writer);

                if (writer.BytesCommitted + writer.BytesPending > MaxEncodedQuestionsBytes)
                {
                    break;
                }
            }

            writer.WriteEndObject();
        }

        if (buffer.WrittenCount > MaxEncodedQuestionsBytes)
        {
            throw new InvalidOperationException(
                $"The plan's questions encode to more than {MaxEncodedQuestionsBytes.ToString("N0", CultureInfo.InvariantCulture)} bytes. Split it into smaller plans.");
        }

        return buffer.WrittenSpan.ToArray();
    }

    private void Register(QuestionDefinition definition)
    {
        if (_questions.Count >= MaxQuestions)
        {
            throw new InvalidOperationException($"A plan holds at most {MaxQuestions.ToString(CultureInfo.InvariantCulture)} questions.");
        }

        _ids.Add(definition.Id);
        _questions.Add(definition);
    }

    private void ThrowIfNotMutable()
    {
        if (_state != State.Mutable)
        {
            throw new InvalidOperationException(_state == State.Frozen
                ? "This builder has already built its plan. Create a new builder for another plan."
                : "This builder failed to build and cannot be used again. Create a new builder.");
        }
    }

    private void ValidateId(string id)
    {
        ArgumentNullException.ThrowIfNull(id);

        if (id.Length == 0)
        {
            throw new ArgumentException("A question id must not be empty.", nameof(id));
        }

        TextRules.ThrowIfMalformed(id, nameof(id), "question id");

        if (_ids.Contains(id))
        {
            throw new ArgumentException("The plan already has a question with this id. Ids are compared ordinally and must be unique.", nameof(id));
        }

        if (_questions.Count >= MaxQuestions)
        {
            throw new InvalidOperationException($"A plan holds at most {MaxQuestions.ToString(CultureInfo.InvariantCulture)} questions.");
        }
    }

    private static void ValidateInstructions(JevContent instructions)
    {
        if (!instructions.IsUnspecified)
        {
            ValidateContent(instructions, nameof(instructions), "instructions", allowNull: true);
        }
    }

    private static void ValidateContent(JevContent content, string paramName, string what, bool allowNull)
    {
        switch (content.Kind)
        {
            case JevContentKind.Unspecified:
                throw new ArgumentException(
                    allowNull
                        ? $"The {what} is unspecified. Use JevContent.Null for none."
                        : $"The {what} is unspecified. It must be text, an object or an array.",
                    paramName);

            case JevContentKind.Null when !allowNull:
                throw new ArgumentException($"The {what} is null, which the contract does not allow here.", paramName);

            case JevContentKind.Text when !content.IsWellFormed:
                throw new ArgumentException($"The {what} contains a lone surrogate, which has no UTF-8 encoding and would be replaced in transit.", paramName);
        }
    }
}
