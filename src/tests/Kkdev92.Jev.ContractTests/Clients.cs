using Kkdev92.Jev.TestSupport;

namespace Kkdev92.Jev.ContractTests;

/// <summary>Clients over a fake handler, configured the way an application would configure them.</summary>
internal static class Clients
{
    public const string ApiKey = "sk-test-0123456789abcdef";

    public static JevClient Create(HttpMessageHandler handler, Action<JevClientOptions>? configure = null)
    {
        var options = new JevClientOptions
        {
            Credential = new StaticJevCredential(ApiKey),

            // Backoff is drawn from zero, so a retry test does not have to advance a clock.
            JitterSource = () => 0,
        };

        configure?.Invoke(options);

        return new JevClient(new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan }, options);
    }

    public enum Tone
    {
        Calm,
        Frustrated,
        Angry,
    }

    public static (JevDecisionPlan Plan, JevChoiceHandle<Tone> Tone, JevNoulHandle Urgent) Plan()
    {
        var builder = new JevDecisionPlanBuilder();
        var tone = builder.AddChoice<Tone>("tone", "What is the customer's tone?", [new(Tone.Calm, "calm"), new(Tone.Frustrated, "frustrated"), new(Tone.Angry, "angry")]);
        var urgent = builder.AddNoul("urgent", "Is this urgent?");
        return (builder.Build(), tone, urgent);
    }

    public static string Answer(double urgent = 0.9) => new SystemOneResponseBuilder()
        .Choice("tone", "frustrated", 0.76, ("calm", 0.0), ("frustrated", 0.84), ("angry", 0.16))
        .Noul("urgent", urgent)
        .Build();
}

/// <summary>Response content whose body never arrives, until the read is cancelled.</summary>
internal sealed class StalledContent : HttpContent
{
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes when the client starts reading the body.</summary>
    public Task Started => _started.Task;

    protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) => await SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context, CancellationToken cancellationToken)
    {
        _started.TrySetResult();
        await Task.Delay(Timeout.Infinite, cancellationToken);
    }

    protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
        => Task.FromResult<Stream>(new StalledStream(_started));

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }

    private sealed class StalledStream(TaskCompletionSource started) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>Response content that streams a body of a given size with no Content-Length, as chunked encoding would.</summary>
internal sealed class UnsizedContent(byte[] body) : HttpContent
{
    protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) => stream.WriteAsync(body, 0, body.Length);

    protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new MemoryStream(body, writable: false));

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }
}

/// <summary>Response content whose <c>Content-Length</c> header says something other than its body.</summary>
internal sealed class MisdeclaredContent : HttpContent
{
    private readonly byte[] _body;

    public MisdeclaredContent(byte[] body, long declared)
    {
        _body = body;
        Headers.ContentLength = declared;
    }

    protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) => stream.WriteAsync(_body, 0, _body.Length);

    protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new MemoryStream(_body, writable: false));

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }
}
