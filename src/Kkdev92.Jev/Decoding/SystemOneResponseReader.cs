using System.Buffers;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text.Json;
using Kkdev92.Jev.Planning;
using Kkdev92.Jev.Serialization;

namespace Kkdev92.Jev.Decoding;

/// <summary>
/// Reads a <c>POST /v1/systemone</c> response straight into the slots of the plan that asked it,
/// checking the contract as it goes.
/// </summary>
/// <remarks>
/// <para>
/// The body is read forward with a <see cref="Utf8JsonReader"/>, synchronously, over a buffer that
/// is already complete; the only look-ahead is each answer's <c>type</c>, found first on a copy of
/// the reader. No DTO graph, no dictionaries: an answer's id and a probability's label are decoded
/// into a stack buffer and looked up in the plan's own tables, and every value lands directly where
/// the result will keep it.
/// </para>
/// <para>
/// What it enforces, independent of field order: every question answered exactly once, nothing
/// answered that was not asked, each answer's <c>type</c> equal to its question's, every option or
/// level given exactly one probability, labels and level keys matched exactly (<c>"0"</c> is a
/// level key, <c>"00"</c> is not), numbers finite and within their documented range to the
/// numerical tolerance, token counts exact non-negative integers, known fields present and not
/// repeated in any spelling (<c>"type"</c> and <c>"\u0074ype"</c> are the same field), and every
/// field name it meets a string that decodes, an unknown field's included. An unknown field is
/// skipped, and nothing inside its value is read.
/// </para>
/// <para>
/// The source-generated wire models decode the same body in <see cref="ReferenceResponseDecoder"/>,
/// and the tests hold the two to the same answers over the same inputs. That reference is how this
/// reader is known to be right; the reader is what makes the call cheap.
/// </para>
/// </remarks>
internal static class SystemOneResponseReader
{
    private const int StackChars = 256;

    private static readonly JsonReaderOptions ReaderOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 64,
    };

    private static readonly JsonDocumentOptions LegendOptions = new()
    {
        AllowDuplicateProperties = false,
        MaxDepth = 64,
    };

    /// <summary>Decodes a body the caller has already checked is valid UTF-8.</summary>
    /// <exception cref="JevProtocolException">The body does not satisfy the contract for this plan.</exception>
    public static DecodedResponse Read(ReadOnlySpan<byte> body, JevDecisionPlan plan, double tolerance)
    {
        var questions = plan.Questions;
        var slots = new AnswerSlot[questions.Length];
        var probabilities = new double[plan.ProbabilityCount];
        var legends = plan.HasScores ? new ImmutableArray<JsonElement>[questions.Length] : null;

        // A plan holds at most 1024 questions, so this is at most 1 KB of stack.
        Span<bool> answered = stackalloc bool[questions.Length];
        Span<char> scratch = stackalloc char[StackChars];

        var reader = new Utf8JsonReader(body, ReaderOptions);

        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                throw Error(JevProtocolError.UnexpectedShape);
            }

            string? model = null;
            JevUsage usage = default;
            bool sawModel = false, sawAnswers = false, sawUsage = false;

            while (Next(ref reader) != JsonTokenType.EndObject)
            {
                if (reader.ValueTextEquals("model"u8))
                {
                    Once(ref sawModel);
                    Expect(Next(ref reader), JsonTokenType.String);
                    model = reader.GetString();
                }
                else if (reader.ValueTextEquals("answers"u8))
                {
                    Once(ref sawAnswers);
                    Expect(Next(ref reader), JsonTokenType.StartObject);
                    ReadAnswers(ref reader, body, plan, slots, probabilities, legends, answered, scratch, tolerance);
                }
                else if (reader.ValueTextEquals("usage"u8))
                {
                    Once(ref sawUsage);
                    Expect(Next(ref reader), JsonTokenType.StartObject);
                    usage = ReadUsage(ref reader, scratch);
                }
                else
                {
                    SkipUnknown(ref reader, scratch);
                }
            }

            // Anything after the root value other than whitespace makes Read throw; a second value
            // cannot come back as a token because multiple values are not enabled.
            if (reader.Read())
            {
                throw Error(JevProtocolError.MalformedJson);
            }

            if (!sawModel || !sawAnswers || !sawUsage)
            {
                throw Error(JevProtocolError.MissingField);
            }

            if (answered.Contains(false))
            {
                throw Error(JevProtocolError.MissingAnswer);
            }

            return new DecodedResponse(model!, usage, slots, probabilities, legends);
        }
        catch (JsonException)
        {
            // Not kept as the inner exception: the reader's message can quote a character of the body.
            throw Error(JevProtocolError.MalformedJson);
        }
        catch (InvalidOperationException)
        {
            // A string that does not decode: an escaped lone surrogate, for one.
            throw Error(JevProtocolError.MalformedJson);
        }
    }

    private static void ReadAnswers(
        ref Utf8JsonReader reader,
        scoped ReadOnlySpan<byte> body,
        JevDecisionPlan plan,
        AnswerSlot[] slots,
        double[] probabilities,
        ImmutableArray<JsonElement>[]? legends,
        scoped Span<bool> answered,
        scoped Span<char> scratch,
        double tolerance)
    {
        while (Next(ref reader) != JsonTokenType.EndObject)
        {
            var slot = Decode(ref reader, scratch, static (plan, key) => plan.SlotOf(key), plan);

            if (slot < 0)
            {
                throw Error(JevProtocolError.UnexpectedAnswer);
            }

            if (answered[slot])
            {
                throw Error(JevProtocolError.DuplicateField);
            }

            answered[slot] = true;
            Expect(Next(ref reader), JsonTokenType.StartObject);

            var question = plan.Questions[slot];
            var type = ScanType(reader);

            if (type != question.Kind)
            {
                throw Error(type == 0 ? JevProtocolError.UnknownAnswerType : JevProtocolError.AnswerTypeMismatch);
            }

            switch (question)
            {
                case NoulQuestionDefinition:
                    slots[slot] = ReadNoul(ref reader, scratch, tolerance);
                    break;

                case ChoiceQuestionDefinition choice:
                    slots[slot] = ReadChoice(ref reader, choice, probabilities, scratch, tolerance);
                    break;

                case ScoreQuestionDefinition score:
                    slots[slot] = ReadScore(ref reader, body, score, probabilities, scratch, out legends![slot], tolerance);
                    break;
            }
        }
    }

    /// <summary>
    /// Finds the answer's <c>type</c> before interpreting anything else, on a copy of the reader.
    /// </summary>
    /// <remarks>
    /// The field may come last. Reading ahead on a copy — a struct copy is an independent cursor over
    /// the same bytes — means a score answer sent for a choice question is reported as a type
    /// mismatch, rather than as whatever its level keys happen to break first. Answers are small, so
    /// the second pass costs little.
    /// </remarks>
    /// <returns>The kind, 0 for a type this SDK does not know.</returns>
    /// <exception cref="JevProtocolException">The type is missing, repeated or not a string.</exception>
    private static QuestionKind ScanType(Utf8JsonReader copy)
    {
        QuestionKind? found = null;

        while (Next(ref copy) != JsonTokenType.EndObject)
        {
            var isType = copy.ValueTextEquals("type"u8);
            Next(ref copy);

            if (!isType)
            {
                copy.Skip();
                continue;
            }

            if (found is not null)
            {
                throw Error(JevProtocolError.DuplicateField);
            }

            Expect(copy.TokenType, JsonTokenType.String);

            found = copy.ValueTextEquals("noul"u8) ? QuestionKind.Noul
                : copy.ValueTextEquals("choice"u8) ? QuestionKind.Choice
                : copy.ValueTextEquals("score"u8) ? QuestionKind.Score
                : 0;
        }

        return found ?? throw Error(JevProtocolError.MissingField);
    }

    private static AnswerSlot ReadNoul(ref Utf8JsonReader reader, scoped Span<char> scratch, double tolerance)
    {
        var slot = new AnswerSlot { Confidence = double.NaN, Selected = -1 };
        bool sawNoul = false;

        while (Next(ref reader) != JsonTokenType.EndObject)
        {
            if (reader.ValueTextEquals("noul"u8))
            {
                Once(ref sawNoul);
                Next(ref reader);
                slot.Value = ReadNumber(ref reader, 1, tolerance);
            }
            else
            {
                // 'type' included: ScanType has already checked it.
                SkipUnknown(ref reader, scratch);
            }
        }

        return sawNoul ? slot : throw Error(JevProtocolError.MissingField);
    }

    private static AnswerSlot ReadChoice(ref Utf8JsonReader reader, ChoiceQuestionDefinition question, double[] probabilities, scoped Span<char> scratch, double tolerance)
    {
        var slot = new AnswerSlot { Value = double.NaN, Selected = -1 };
        bool sawChoice = false, sawConfidence = false, sawProbabilities = false;

        while (Next(ref reader) != JsonTokenType.EndObject)
        {
            if (reader.ValueTextEquals("choice"u8))
            {
                Once(ref sawChoice);
                Expect(Next(ref reader), JsonTokenType.String);
                slot.Selected = Decode(ref reader, scratch, static (question, label) => question.IndexOf(label), question);

                if (slot.Selected < 0)
                {
                    throw Error(JevProtocolError.UnknownLabel);
                }
            }
            else if (reader.ValueTextEquals("confidence"u8))
            {
                Once(ref sawConfidence);
                Next(ref reader);
                slot.Confidence = ReadNumber(ref reader, 1, tolerance);
            }
            else if (reader.ValueTextEquals("probabilities"u8))
            {
                Once(ref sawProbabilities);
                Expect(Next(ref reader), JsonTokenType.StartObject);
                ReadChoiceProbabilities(ref reader, question, probabilities, scratch, tolerance);
            }
            else
            {
                SkipUnknown(ref reader, scratch);
            }
        }

        return sawChoice && sawConfidence && sawProbabilities ? slot : throw Error(JevProtocolError.MissingField);
    }

    private static void ReadChoiceProbabilities(ref Utf8JsonReader reader, ChoiceQuestionDefinition question, double[] probabilities, scoped Span<char> scratch, double tolerance)
    {
        // At most 255 options.
        Span<bool> seen = stackalloc bool[question.Labels.Length];
        var count = 0;

        while (Next(ref reader) != JsonTokenType.EndObject)
        {
            var index = Decode(ref reader, scratch, static (question, label) => question.IndexOf(label), question);

            if (index < 0)
            {
                throw Error(JevProtocolError.UnknownLabel);
            }

            if (seen[index])
            {
                throw Error(JevProtocolError.DuplicateField);
            }

            seen[index] = true;
            count++;
            Next(ref reader);
            probabilities[question.ProbabilityOffset + index] = ReadNumber(ref reader, 1, tolerance);
        }

        if (count != question.Labels.Length)
        {
            // An option with no probability is not an option with probability zero.
            throw Error(JevProtocolError.MissingProbability);
        }
    }

    private static AnswerSlot ReadScore(ref Utf8JsonReader reader, scoped ReadOnlySpan<byte> body, ScoreQuestionDefinition question, double[] probabilities, scoped Span<char> scratch, out ImmutableArray<JsonElement> legend, double tolerance)
    {
        var slot = new AnswerSlot { Selected = -1 };
        bool sawScore = false, sawConfidence = false, sawLegend = false, sawProbabilities = false;
        var levels = question.Levels.Length;
        legend = default;

        while (Next(ref reader) != JsonTokenType.EndObject)
        {
            if (reader.ValueTextEquals("score"u8))
            {
                Once(ref sawScore);
                Next(ref reader);
                slot.Value = ReadNumber(ref reader, levels - 1, tolerance);
            }
            else if (reader.ValueTextEquals("confidence"u8))
            {
                Once(ref sawConfidence);
                Next(ref reader);
                slot.Confidence = ReadNumber(ref reader, 1, tolerance);
            }
            else if (reader.ValueTextEquals("legend"u8))
            {
                Once(ref sawLegend);
                Expect(Next(ref reader), JsonTokenType.StartObject);
                legend = ReadLegend(ref reader, body, levels);
            }
            else if (reader.ValueTextEquals("probabilities"u8))
            {
                Once(ref sawProbabilities);
                Expect(Next(ref reader), JsonTokenType.StartObject);
                ReadLevelProbabilities(ref reader, question, probabilities, tolerance);
            }
            else
            {
                SkipUnknown(ref reader, scratch);
            }
        }

        return sawScore && sawConfidence && sawLegend && sawProbabilities ? slot : throw Error(JevProtocolError.MissingField);
    }

    private static void ReadLevelProbabilities(ref Utf8JsonReader reader, ScoreQuestionDefinition question, double[] probabilities, double tolerance)
    {
        Span<bool> seen = stackalloc bool[question.Levels.Length];
        var count = 0;

        while (Next(ref reader) != JsonTokenType.EndObject)
        {
            var level = ReadLevelKey(ref reader, question.Levels.Length);

            if (seen[level])
            {
                throw Error(JevProtocolError.DuplicateField);
            }

            seen[level] = true;
            count++;
            Next(ref reader);
            probabilities[question.ProbabilityOffset + level] = ReadNumber(ref reader, 1, tolerance);
        }

        if (count != question.Levels.Length)
        {
            throw Error(JevProtocolError.MissingProbability);
        }
    }

    /// <summary>
    /// Keeps each level's description as its own <see cref="JsonElement"/>, parsed from the exact
    /// bytes of the value into a document that does not share the response buffer.
    /// </summary>
    private static ImmutableArray<JsonElement> ReadLegend(ref Utf8JsonReader reader, scoped ReadOnlySpan<byte> body, int levels)
    {
        var values = new JsonElement[levels];
        Span<bool> seen = stackalloc bool[levels];
        var count = 0;

        while (Next(ref reader) != JsonTokenType.EndObject)
        {
            var level = ReadLevelKey(ref reader, levels);

            if (seen[level])
            {
                throw Error(JevProtocolError.DuplicateField);
            }

            seen[level] = true;
            count++;

            var token = Next(ref reader);

            // The contract admits a string, an object or an array; not null, a number or a boolean.
            if (token is not (JsonTokenType.String or JsonTokenType.StartObject or JsonTokenType.StartArray))
            {
                throw Error(JevProtocolError.UnexpectedShape);
            }

            // For a string the start index is the opening quote; for an object or an array, Skip
            // moves to the closing bracket. Either way the slice is exactly the value.
            var start = (int)reader.TokenStartIndex;
            reader.Skip();
            var end = (int)reader.BytesConsumed;

            values[level] = JsonElement.Parse(body[start..end], LegendOptions);
        }

        return count == levels
            ? ImmutableCollectionsMarshal.AsImmutableArray(values)
            : throw Error(JevProtocolError.MissingLegend);
    }

    /// <summary>
    /// A level key: the decimal index of a level, spelled canonically. <c>"0"</c>, never <c>"00"</c>
    /// or <c>"0.0"</c> — the key is matched as text, not as a number.
    /// </summary>
    private static int ReadLevelKey(ref Utf8JsonReader reader, int levels)
    {
        Span<char> key = stackalloc char[16];

        // Unescaped text is never longer than its escaped bytes, so this bounds the copy.
        if (reader.ValueSpan.Length > key.Length)
        {
            throw Error(JevProtocolError.InvalidLevel);
        }

        var length = reader.CopyString(key);
        key = key[..length];

        if (length == 0 || length > 2 || (length > 1 && key[0] == '0') || key.ContainsAnyExceptInRange('0', '9'))
        {
            throw Error(JevProtocolError.InvalidLevel);
        }

        var level = length == 1 ? key[0] - '0' : ((key[0] - '0') * 10) + (key[1] - '0');

        return level < levels ? level : throw Error(JevProtocolError.InvalidLevel);
    }

    private static JevUsage ReadUsage(ref Utf8JsonReader reader, scoped Span<char> scratch)
    {
        long input = 0, output = 0;
        bool sawInput = false, sawOutput = false;

        while (Next(ref reader) != JsonTokenType.EndObject)
        {
            if (reader.ValueTextEquals("input_tokens"u8))
            {
                Once(ref sawInput);
                Next(ref reader);
                input = ReadCount(ref reader);
            }
            else if (reader.ValueTextEquals("output_tokens"u8))
            {
                Once(ref sawOutput);
                Next(ref reader);
                output = ReadCount(ref reader);
            }
            else
            {
                SkipUnknown(ref reader, scratch);
            }
        }

        return sawInput && sawOutput ? new JevUsage(input, output) : throw Error(JevProtocolError.MissingField);
    }

    /// <summary>A token count: an exact, non-negative integer, never read through a double.</summary>
    private static long ReadCount(ref Utf8JsonReader reader)
    {
        Expect(reader.TokenType, JsonTokenType.Number);

        // A number token is never escaped, so its bytes are its text.
        return JsonExactInteger.TryParseInt64(reader.ValueSpan, out var value) && value >= 0
            ? value
            : throw Error(JevProtocolError.InvalidTokenCount);
    }

    /// <summary>A finite number between 0 and <paramref name="maximum"/>, within the tolerance.</summary>
    private static double ReadNumber(ref Utf8JsonReader reader, double maximum, double tolerance)
    {
        Expect(reader.TokenType, JsonTokenType.Number);

        // TryGetDouble reports success for 1e400 and hands back infinity — measured on .NET 10.0.12 —
        // so finiteness is checked here rather than assumed.
        if (!reader.TryGetDouble(out var value) || !double.IsFinite(value))
        {
            throw Error(JevProtocolError.NonFiniteNumber);
        }

        return value >= -tolerance && value <= maximum + tolerance
            ? value
            : throw Error(JevProtocolError.NumberOutOfRange);
    }

    /// <summary>Skips a field this reader does not know, once its name is known to decode.</summary>
    /// <remarks>
    /// An unknown name is compared to nothing, but it is still a JSON string, and one that does not
    /// decode — an escaped lone surrogate — makes the body malformed wherever it sits, as it does
    /// for the reference decoder. Without this the outcome would rest on an accident:
    /// <see cref="Utf8JsonReader.ValueTextEquals(ReadOnlySpan{byte})"/> only unescapes a name whose
    /// length and leading characters could still match, so the same malformed name would be refused
    /// beside <c>answers</c> and let through beside <c>usage</c>. Only an escaped name is decoded,
    /// so a plain one costs nothing more.
    /// </remarks>
    private static void SkipUnknown(ref Utf8JsonReader reader, scoped Span<char> scratch)
    {
        if (reader.ValueIsEscaped)
        {
            // Throws InvalidOperationException for a string that does not decode.
            Decode(ref reader, scratch, static (_, _) => 0, 0);
        }

        Next(ref reader);
        reader.Skip();
    }

    /// <summary>
    /// Unescapes the current string or property name into the scratch buffer — or a rented one when
    /// it does not fit — and looks it up, without allocating a string.
    /// </summary>
    private static int Decode<TState>(ref Utf8JsonReader reader, scoped Span<char> scratch, Func<TState, ReadOnlySpan<char>, int> lookup, TState state)
    {
        var maxChars = reader.ValueSpan.Length;

        if (maxChars <= scratch.Length)
        {
            var length = reader.CopyString(scratch);
            return lookup(state, scratch[..length]);
        }

        var rented = ArrayPool<char>.Shared.Rent(maxChars);

        try
        {
            var length = reader.CopyString(rented);
            return lookup(state, rented.AsSpan(0, length));
        }
        finally
        {
            ArrayPool<char>.Shared.Return(rented);
        }
    }

    /// <summary>Advances one token. The body is complete, so running out is malformed JSON.</summary>
    private static JsonTokenType Next(ref Utf8JsonReader reader)
        => reader.Read() ? reader.TokenType : throw Error(JevProtocolError.MalformedJson);

    private static void Expect(JsonTokenType actual, JsonTokenType expected)
    {
        if (actual != expected)
        {
            throw Error(JevProtocolError.UnexpectedShape);
        }
    }

    private static void Once(ref bool seen)
    {
        if (seen)
        {
            throw Error(JevProtocolError.DuplicateField);
        }

        seen = true;
    }

    private static JevProtocolException Error(JevProtocolError error) => new(JevOperation.Evaluate, error);
}
