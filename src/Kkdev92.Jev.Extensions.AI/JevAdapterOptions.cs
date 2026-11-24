using Microsoft.Extensions.AI;

namespace Kkdev92.Jev.Extensions.AI;

/// <summary>
/// Which text of a conversation is sent to the judge, and the limits on reading it. Copied when the
/// policy, router or evaluator that takes it is created.
/// </summary>
public sealed class JevChatStateOptions
{
    /// <summary>
    /// Which messages are sent. <see cref="JevChatStateScope.Unspecified"/>, the default, takes the default
    /// of wherever the options are used.
    /// </summary>
    public JevChatStateScope Scope { get; set; } = JevChatStateScope.Unspecified;
    /// <summary>
    /// The roles whose messages <see cref="JevChatStateScope.ConversationText"/> sends; user and assistant
    /// when <see langword="null"/>. Valid only with that scope.
    /// </summary>
    public IReadOnlyList<ChatRole>? IncludedRoles { get; set; }
    /// <summary>
    /// What happens to content other than text in the messages sent: refused, the default, or skipped.
    /// </summary>
    public JevUnsupportedContentBehavior UnsupportedContentBehavior { get; set; }
    = JevUnsupportedContentBehavior.Reject;
    /// <summary>The most UTF-8 bytes of state sent to the judge. 1 MiB by default.</summary>
    public int MaxStateUtf8Bytes { get; set; } = 1_048_576;
    /// <summary>The most messages read, the request's and the reply's together. 256 by default.</summary>
    public int MaxMessages { get; set; } = 256;
    /// <summary>The most content items read across those messages. 4,096 by default.</summary>
    public int MaxContentItems { get; set; } = 4096;
    /// <summary>
    /// Builds the state from the request's messages and, for an output policy or an evaluator, the
    /// reply. Required with <see cref="JevChatStateScope.Custom"/>, and valid only with it.
    /// </summary>
    public Func<IReadOnlyList<ChatMessage>, ChatResponse?, JevContent>? Projector { get; set; }
}

/// <summary>
/// How a guarded client answers a blocked request or reply, and holds a streamed reply for its output
/// policy. Copied when the client is created.
/// </summary>
public sealed class JevGuardOptions
{
    /// <summary>
    /// One deadline over the whole guarded call — the judgments and the provider's generation — after
    /// which it fails with <see cref="JevPipelineTimeoutException"/>. None when <see langword="null"/>,
    /// the default; each judge call keeps the client's own deadline either way.
    /// </summary>
    public TimeSpan? TotalTimeout { get; set; }
    /// <summary>The clock <see cref="TotalTimeout"/> is measured with.</summary>
    public TimeProvider TimeProvider { get; set; } = System.TimeProvider.System;
    /// <summary>Whether a blocked request or reply throws, the default, or is answered with <see cref="BlockedMessage"/>.</summary>
    public JevBlockBehavior BlockBehavior { get; set; } = JevBlockBehavior.Throw;
    /// <summary>
    /// The assistant message returned in place of a blocked request or reply when
    /// <see cref="BlockBehavior"/> is <see cref="JevBlockBehavior.ReturnMessage"/>, which requires it.
    /// </summary>
    public string? BlockedMessage { get; set; }
    /// <summary>
    /// Whether a streaming call under an output policy is refused, the default, or buffered until the
    /// reply has been judged.
    /// </summary>
    public JevOutputStreamingMode OutputStreamingMode { get; set; }
    = JevOutputStreamingMode.RejectStreaming;
    /// <summary>
    /// The most UTF-8 bytes a buffered reply may take, its updates measured as JSON. 1 MiB by default.
    /// </summary>
    public int MaxBufferedUtf8Bytes { get; set; } = 1_048_576;
    /// <summary>The most updates of a streamed reply held for the output policy. 1,024 by default.</summary>
    public int MaxBufferedUpdates { get; set; } = 1024;
    /// <summary>The most content items held across those updates. 4,096 by default.</summary>
    public int MaxBufferedContentItems { get; set; } = 4096;
}

/// <summary>What a router sends to the judge, and its deadline. Copied when the router is created.</summary>
public sealed class JevRoutingOptions
{
    /// <summary>Which text of the request is sent to the judge; the last user message by default.</summary>
    public JevChatStateOptions? StateOptions { get; set; }
    /// <summary>
    /// Options for the judge's call, such as the model or its deadline. Capturing a response or an error
    /// body is refused.
    /// </summary>
    public JevRequestOptions? RequestOptions { get; set; }
    /// <summary>
    /// One deadline over the routing judgment and the chosen client's generation, after which the call
    /// fails with <see cref="JevPipelineTimeoutException"/>. None when <see langword="null"/>, the default.
    /// </summary>
    public TimeSpan? TotalTimeout { get; set; }
    /// <summary>The clock <see cref="TotalTimeout"/> is measured with.</summary>
    public TimeProvider TimeProvider { get; set; } = System.TimeProvider.System;
}
