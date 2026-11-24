namespace Kkdev92.Jev.Extensions.AI;

/// <summary>Outcome of a guard policy.</summary>
public enum JevGuardDecision
{
    /// <summary>Not a decision. A projection that returns it fails with <see cref="JevProjectionException"/>.</summary>
    Unspecified = 0,
    /// <summary>Let the request through to the provider, or the reply back to the caller.</summary>
    Allow = 1,
    /// <summary>Stop with <see cref="JevReviewRequiredException"/>, for a person or another process to decide.</summary>
    Review = 2,
    /// <summary>
    /// Refuse: throw <see cref="JevGuardRejectedException"/>, or answer with
    /// <see cref="JevGuardOptions.BlockedMessage"/> when <see cref="JevGuardOptions.BlockBehavior"/> is
    /// <see cref="JevBlockBehavior.ReturnMessage"/>.
    /// </summary>
    Block = 3,
}

/// <summary>What a guarded client does when a policy decides <see cref="JevGuardDecision.Block"/>.</summary>
public enum JevBlockBehavior
{
    /// <summary>Throw <see cref="JevGuardRejectedException"/>. The default.</summary>
    Throw = 0,
    /// <summary>
    /// Answer with <see cref="JevGuardOptions.BlockedMessage"/> as an assistant message, with the finish
    /// reason <c>ChatFinishReason.ContentFilter</c>, in place of the provider's reply.
    /// </summary>
    ReturnMessage = 1,
}

/// <summary>How a streaming call is handled when an output policy has to judge the reply.</summary>
public enum JevOutputStreamingMode
{
    /// <summary>Refuse the streaming call with <see cref="NotSupportedException"/>. The default.</summary>
    RejectStreaming = 0,
    /// <summary>
    /// Hold every update of the streamed reply, within the buffer limits of <see cref="JevGuardOptions"/>,
    /// until the output policy has judged the whole reply; then release them, or the blocked message
    /// instead.
    /// </summary>
    Buffer = 1,
}

/// <summary>What happens to content other than text in the messages sent to the judge.</summary>
public enum JevUnsupportedContentBehavior
{
    /// <summary>
    /// Fail with <see cref="JevAdapterFailureReason.UnsupportedContent"/> when a message to be sent holds
    /// content other than text. The default.
    /// </summary>
    Reject = 0,
    /// <summary>Send only the text of the messages, and skip any other content.</summary>
    TextOnly = 1,
}

/// <summary>Which text of a conversation is sent to the judge.</summary>
/// <remarks>
/// Input policies and routers take <see cref="LatestUserText"/>, <see cref="ConversationText"/> or
/// <see cref="Custom"/>; output policies and evaluators take <see cref="ResponseText"/>,
/// <see cref="RequestAndResponseText"/> or <see cref="Custom"/>. A scope used where it does not apply
/// is refused when the policy, router or evaluator is created.
/// </remarks>
public enum JevChatStateScope
{
    /// <summary>
    /// The default of wherever the options are used: <see cref="LatestUserText"/> for an input policy
    /// and a router, <see cref="ResponseText"/> for an output policy, and
    /// <see cref="RequestAndResponseText"/> for an evaluator.
    /// </summary>
    Unspecified = 0,
    /// <summary>The last user message of the request.</summary>
    LatestUserText = 1,
    /// <summary>
    /// Every message of the request whose role is in <see cref="JevChatStateOptions.IncludedRoles"/>,
    /// user and assistant by default.
    /// </summary>
    ConversationText = 2,
    /// <summary>The assistant messages of the reply.</summary>
    ResponseText = 3,
    /// <summary>The last user message of the request and the assistant messages of the reply.</summary>
    RequestAndResponseText = 4,
    /// <summary>Whatever <see cref="JevChatStateOptions.Projector"/> returns.</summary>
    Custom = 5,
}

/// <summary>Outcome of a routing decision.</summary>
public enum JevRouteKind
{
    /// <summary>Not a decision. A projection that returns it fails with <see cref="JevProjectionException"/>.</summary>
    Unspecified = 0,
    /// <summary>Send the request to the client registered for <see cref="JevRouteDecision{TRoute}.Route"/>.</summary>
    Route = 1,
    /// <summary>
    /// Send the request to the fallback client. Without one, the call fails with
    /// <see cref="JevAdapterFailureReason.MissingFallback"/>.
    /// </summary>
    Fallback = 2,
    /// <summary>Stop with <see cref="JevReviewRequiredException"/> instead of calling any client.</summary>
    Review = 3,
}

/// <summary>Where in a guarded, routed, function or evaluation call a failure happened.</summary>
public enum JevAdapterStage
{
    /// <summary>No stage.</summary>
    Unspecified = 0,
    /// <summary>Reading the messages and building the state sent to the judge.</summary>
    StateProjection = 1,
    /// <summary>Turning the judge's result into a decision or a value, in a projection of yours.</summary>
    ResultProjection = 2,
    /// <summary>Judging the request before the provider is called.</summary>
    InputGuard = 3,
    /// <summary>Judging the request to pick a route.</summary>
    Routing = 4,
    /// <summary>The provider generating the reply.</summary>
    Generation = 5,
    /// <summary>Judging the reply before it is returned.</summary>
    OutputGuard = 6,
    /// <summary>Running a decision function that a model called.</summary>
    FunctionInvocation = 7,
    /// <summary>Judging a request and its reply for an evaluator.</summary>
    Evaluation = 8,
    /// <summary>Holding a streamed reply until the output policy has judged it.</summary>
    BufferedStreaming = 9,
    /// <summary>Querying, disposing or cleaning up after the wrapped client.</summary>
    Lifecycle = 10,
}

/// <summary>Why a guarded, routed, function or evaluation call failed. Never carries content.</summary>
public enum JevAdapterFailureReason
{
    /// <summary>No reason.</summary>
    Unspecified = 0,
    /// <summary>A projection of yours, of the state or of the result, threw or returned something unusable.</summary>
    InvalidProjection = 1,
    /// <summary>The state to judge held no text.</summary>
    NoAnalyzableText = 2,
    /// <summary>
    /// A message held something that cannot be sent to the judge — content other than text, malformed
    /// text, a reply's text under a role other than assistant — or a reply came with a continuation
    /// token.
    /// </summary>
    UnsupportedContent = 3,
    /// <summary>
    /// The messages or the state exceeded <see cref="JevChatStateOptions.MaxMessages"/>,
    /// <see cref="JevChatStateOptions.MaxContentItems"/> or <see cref="JevChatStateOptions.MaxStateUtf8Bytes"/>.
    /// </summary>
    StateLimitExceeded = 4,
    /// <summary>A projection returned <c>Unspecified</c>, or a value outside its enumeration.</summary>
    InvalidDecision = 5,
    /// <summary>The decision named a route with no client registered for it.</summary>
    UnknownRoute = 6,
    /// <summary>The decision chose the fallback, and no fallback client was given.</summary>
    MissingFallback = 7,
    /// <summary>
    /// A streamed update held something that cannot be held for the output policy: content other than
    /// text and usage, malformed text, or a continuation token.
    /// </summary>
    UnsupportedStreamingContent = 8,
    /// <summary>
    /// A streamed reply exceeded <see cref="JevGuardOptions.MaxBufferedUtf8Bytes"/>,
    /// <see cref="JevGuardOptions.MaxBufferedUpdates"/> or <see cref="JevGuardOptions.MaxBufferedContentItems"/>.
    /// </summary>
    BufferLimitExceeded = 9,
    /// <summary>The provider or the wrapped client threw, or returned nothing.</summary>
    DownstreamFailure = 10,
    /// <summary>Something was canceled without the caller or the deadline asking for it.</summary>
    UnexpectedCancellation = 11,
    /// <summary>
    /// A function call did not pass exactly one argument, <c>state</c>, a string that is not blank and
    /// fits <see cref="JevFunctionOptions.MaxInputUtf8Bytes"/>.
    /// </summary>
    InvalidFunctionArguments = 12,
    /// <summary>A function's serialized result exceeded <see cref="JevFunctionOptions.MaxOutputUtf8Bytes"/>.</summary>
    ResultLimitExceeded = 13,
    /// <summary>
    /// Additional evaluation context was passed to an evaluator without a context projector, or more of
    /// it than the evaluator accepts.
    /// </summary>
    UnsupportedEvaluationContext = 14,
    /// <summary>
    /// The judge failed, and its exception was replaced rather than passed on, because it could carry
    /// more than a reason: an error body, an inner exception, or a failure other than an evaluation's.
    /// </summary>
    EvaluationUnavailable = 15,
    /// <summary>A guard decided <see cref="JevGuardDecision.Block"/>.</summary>
    GuardBlocked = 16,
    /// <summary>A guard or a router decided that a review is needed.</summary>
    ReviewRequired = 17,
    /// <summary>The call's total timeout passed.</summary>
    DeadlineExceeded = 18,
}
