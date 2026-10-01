// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Tests;

using System.Net;
using System.Text;
using System.Text.Json;
using CodexBar.Core.Security;

public sealed class AuthenticationErrorFormatterTests
{
    private const string Secret = "SENSITIVE_PAYLOAD_MARKER";

    [Theory]
    [InlineData("invalid_request")]
    [InlineData("invalid_client")]
    [InlineData("invalid_grant")]
    [InlineData("unauthorized_client")]
    [InlineData("unsupported_grant_type")]
    [InlineData("invalid_scope")]
    [InlineData("access_denied")]
    [InlineData("server_error")]
    [InlineData("temporarily_unavailable")]
    [InlineData("authorization_pending")]
    [InlineData("slow_down")]
    [InlineData("expired_token")]
    [InlineData("unsupported_response_type")]
    [InlineData("authentication_error")]
    [InlineData("permission_error")]
    [InlineData("rate_limit_error")]
    [InlineData("invalid_request_error")]
    [InlineData("not_found_error")]
    [InlineData("overloaded_error")]
    [InlineData("api_error")]
    [InlineData("invalid_token")]
    [InlineData("insufficient_scope")]
    public void FormatResponse_KnownError_PreservesOnlyStatusAndCode(string code)
    {
        var body = $$"""{"error":"{{code}}","error_description":"{{Secret}}","access_token":"{{Secret}}","refresh_token":"{{Secret}}","authorization_code":"{{Secret}}","cookie":"session={{Secret}}"}""";

        var result = AuthenticationErrorFormatter.FormatResponse(HttpStatusCode.BadRequest, body);

        Assert.Equal($"Provider request failed (HTTP 400; {code}).", result);
        Assert.DoesNotContain(Secret, result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("123")]
    [InlineData("{}")]
    [InlineData("{\"error\":null}")]
    [InlineData("{\"error\":{}}")]
    [InlineData("{\"error\":{\"type\":123}}")]
    [InlineData("{\"error\":\"SENSITIVE_PAYLOAD_MARKER\"}")]
    [InlineData("{\"error\":{\"type\":\"SENSITIVE_PAYLOAD_MARKER\"}}")]
    [InlineData("<html>SENSITIVE_PAYLOAD_MARKER</html>")]
    public void FormatResponse_UnexpectedBody_DiscardsBody(string? body)
    {
        Assert.Equal("Provider request failed (HTTP 403).", AuthenticationErrorFormatter.FormatResponse(HttpStatusCode.Forbidden, body));
    }

    [Fact]
    public void FormatResponse_NestedProviderError_PreservesSafeType()
    {
        var body = JsonSerializer.Serialize(new { error = new { type = "permission_error", message = Secret } });

        Assert.Equal("Provider request failed (HTTP 403; permission_error).", AuthenticationErrorFormatter.FormatResponse(HttpStatusCode.Forbidden, body));
    }

    [Fact]
    public void FormatResponse_OversizedBody_DiscardsBody()
    {
        Assert.Equal("Provider request failed (HTTP 500).", AuthenticationErrorFormatter.FormatResponse(HttpStatusCode.InternalServerError, new string('x', AuthenticationErrorFormatter.MaximumBodyBytes + 1)));
    }

    [Theory]
    [InlineData("{\"error\":\"invalid_grant\",\"access_token\":\"SENSITIVE_PAYLOAD_MARKER\"}", "Provider request failed (HTTP 401; invalid_grant).")]
    [InlineData("", "Provider request failed (HTTP 401).")]
    public async Task FormatResponseAsync_ReadableBody_ReturnsSafeDiagnostic(string body, string expected)
    {
        using var response = new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent(body) };

        Assert.Equal(expected, await AuthenticationErrorFormatter.FormatResponseAsync(response));
    }

    [Fact]
    public async Task FormatResponseAsync_OversizedBody_DiscardsBody()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent(new string('x', AuthenticationErrorFormatter.MaximumBodyBytes + 1)) };

        Assert.Equal("Provider request failed (HTTP 400).", await AuthenticationErrorFormatter.FormatResponseAsync(response));
    }

    [Fact]
    public async Task FormatResponseAsync_UnreadableBody_DiscardsException()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new FailingContent() };

        Assert.Equal("Provider request failed (HTTP 400).", await AuthenticationErrorFormatter.FormatResponseAsync(response));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FormatResponse_ExactByteBoundary_PreservesCodeOnlyWithinLimit(bool multibyte, bool oversized)
    {
        var prefix = "{\"error\":\"invalid_token\",\"padding\":\"" + new string(multibyte ? '\u2603' : 'x', 2000) + "\"}";
        var byteLength = AuthenticationErrorFormatter.MaximumBodyBytes + (oversized ? 1 : 0);
        var body = prefix + new string(' ', byteLength - Encoding.UTF8.GetByteCount(prefix));
        Assert.Equal(byteLength, Encoding.UTF8.GetByteCount(body));
        var expected = oversized ? "Provider request failed (HTTP 401)." : "Provider request failed (HTTP 401; invalid_token).";
        using var response = new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent(body) };

        Assert.Equal(expected, AuthenticationErrorFormatter.FormatResponse(response.StatusCode, body));
        Assert.Equal(expected, await AuthenticationErrorFormatter.FormatResponseAsync(response));
    }

    [Fact]
    public async Task FormatResponseAsync_CallerCanceledDuringRead_PropagatesTokenAndCancellation()
    {
        using var stream = new WaitingStream();
        using var response = new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StreamContent(stream) };
        using var cts = new CancellationTokenSource();
        var reading = AuthenticationErrorFormatter.FormatResponseAsync(response, cts.Token);
        await stream.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(cts.Token, stream.ObservedToken);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void FormatException_HttpFailure_PreservesOnlyStatus()
    {
        var error = new HttpRequestException(Secret, new Exception(Secret), HttpStatusCode.TooManyRequests);

        Assert.Equal("Provider request failed (HTTP 429).", AuthenticationErrorFormatter.FormatException(error));
    }

    [Fact]
    public void FormatException_Timeout_DiscardsMessage()
    {
        Assert.Equal("Provider request timed out. Try again.", AuthenticationErrorFormatter.FormatException(new TaskCanceledException(Secret)));
    }

    [Fact]
    public void FormatException_UnknownFailure_DiscardsMessageAndInnerException()
    {
        Assert.Equal("Provider request failed. Retry or sign in again.", AuthenticationErrorFormatter.FormatException(new Exception(Secret, new Exception(Secret))));
    }

    [Fact]
    public void FormatCommandFailure_NonzeroExit_PreservesExitAndAction()
    {
        Assert.Equal("Authentication command failed (exit 1). Run 'gh auth login' and try again.", AuthenticationErrorFormatter.FormatCommandFailure(1));
    }

    private sealed class FailingContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new IOException(Secret);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class WaitingStream : Stream
    {
        private readonly CancellationTokenSource _disposal = new();
        private bool _disposed;

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken ObservedToken { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            this.ObservedToken = cancellationToken;
            this.Started.SetResult();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, this._disposal.Token);
            await Task.Delay(Timeout.InfiniteTimeSpan, linked.Token);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !this._disposed)
            {
                this._disposed = true;
                this._disposal.Cancel();
                this._disposal.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
