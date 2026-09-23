using Kkdev92.Jev.Internal;

namespace Kkdev92.Jev;

/// <summary>A fixed API key, given explicitly.</summary>
/// <remarks>
/// The client never goes looking for a key on its own — not in environment variables, not in a
/// user profile, not in a secret store. <see cref="FromEnvironmentVariable"/> exists for the cases
/// where that is where the key lives, and has to be called to have any effect.
/// </remarks>
public sealed class StaticJevCredential : IJevCredential
{
    /// <summary>The variable TypeSafe's quickstart uses for the key.</summary>
    public const string DefaultEnvironmentVariable = "TYPESAFE_API_KEY";

    private readonly string _apiKey;

    /// <summary>Wraps a key.</summary>
    /// <param name="apiKey">The key: printable ASCII with no whitespace.</param>
    /// <exception cref="ArgumentNullException"><paramref name="apiKey"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The key is empty or contains whitespace, control or non-ASCII characters. The message never contains it.</exception>
    public StaticJevCredential(string apiKey)
    {
        ArgumentNullException.ThrowIfNull(apiKey);

        if (!TextRules.IsUsableApiKey(apiKey))
        {
            throw new ArgumentException("The API key is empty, or contains whitespace, control or non-ASCII characters.", nameof(apiKey));
        }

        _apiKey = apiKey;
    }

    /// <summary>Reads a key from an environment variable, now, once.</summary>
    /// <param name="variable">The variable to read. Defaults to <see cref="DefaultEnvironmentVariable"/>.</param>
    /// <exception cref="InvalidOperationException">The variable is not set, or its value is not a usable key.</exception>
    public static StaticJevCredential FromEnvironmentVariable(string variable = DefaultEnvironmentVariable)
    {
        ArgumentException.ThrowIfNullOrEmpty(variable);

        var value = Environment.GetEnvironmentVariable(variable);

        if (string.IsNullOrEmpty(value))
        {
            throw new InvalidOperationException($"The environment variable '{variable}' is not set.");
        }

        // A value read from a file or a shell often ends with a newline; that is trimmed, and
        // nothing else is.
        value = value.Trim();

        return TextRules.IsUsableApiKey(value)
            ? new StaticJevCredential(value)
            : throw new InvalidOperationException($"The environment variable '{variable}' does not hold a usable API key.");
    }

    /// <inheritdoc />
    public ValueTask<string> GetApiKeyAsync(CancellationToken cancellationToken) => ValueTask.FromResult(_apiKey);

    /// <summary>The type name. Never the key.</summary>
    public override string ToString() => nameof(StaticJevCredential);
}
