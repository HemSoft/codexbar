// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Tests;

using System.Net;
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
        Assert.Equal("Provider request failed (HTTP 500).", AuthenticationErrorFormatter.FormatResponse(HttpStatusCode.InternalServerError, new string('x', 8193)));
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
        using var response = new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent(new string('x', 8193)) };

        Assert.Equal("Provider request failed (HTTP 400).", await AuthenticationErrorFormatter.FormatResponseAsync(response));
    }

    [Fact]
    public async Task FormatResponseAsync_UnreadableBody_DiscardsException()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new FailingContent() };

        Assert.Equal("Provider request failed (HTTP 400).", await AuthenticationErrorFormatter.FormatResponseAsync(response));
    }

    [Fact]
    public async Task FormatResponseAsync_CallerCanceled_PropagatesCancellation()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new CancelingContent() };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AuthenticationErrorFormatter.FormatResponseAsync(response, cts.Token));
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

    private sealed class CancelingContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new OperationCanceledException(Secret);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
