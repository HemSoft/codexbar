// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Security;

using System.Net;
using System.Text;
using System.Text.Json;

/// <summary>Formats authentication diagnostics without retaining untrusted bodies or exception details.</summary>
internal static class AuthenticationErrorFormatter
{
    private const int MaximumBodyBytes = 8192;

    internal static string FormatResponse(HttpStatusCode status, string? body)
    {
        var code = ReadSafeErrorCode(body);
        return code is null
            ? $"Provider request failed (HTTP {(int)status})."
            : $"Provider request failed (HTTP {(int)status}; {code}).";
    }

    internal static async Task<string> FormatResponseAsync(HttpResponseMessage response, CancellationToken ct = default)
    {
        try
        {
            using var stream = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[MaximumBodyBytes + 1];
            var length = 0;
            while (length < buffer.Length)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(length), ct);
                if (count == 0)
                {
                    break;
                }

                length += count;
            }

            var body = length > MaximumBodyBytes ? null : Encoding.UTF8.GetString(buffer, 0, length);
            return FormatResponse(response.StatusCode, body);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return FormatResponse(response.StatusCode, null);
        }
    }

    internal static string FormatException(Exception exception) => exception switch
    {
        HttpRequestException { StatusCode: { } status } => FormatResponse(status, null),
        OperationCanceledException => "Provider request timed out. Try again.",
        _ => "Provider request failed. Retry or sign in again.",
    };

    internal static string FormatCommandFailure(int exitCode) =>
        $"Authentication command failed (exit {exitCode}). Run 'gh auth login' and try again.";

    private static string? ReadSafeErrorCode(string? body)
    {
        if (string.IsNullOrWhiteSpace(body) || body.Length > MaximumBodyBytes)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("error", out var error))
            {
                return null;
            }

            var code = error.ValueKind == JsonValueKind.String ? error.GetString() : ReadNestedCode(error);
            return code switch
            {
                "invalid_request" or "invalid_client" or "invalid_grant" or "unauthorized_client"
                    or "unsupported_grant_type" or "invalid_scope" or "access_denied" or "server_error"
                    or "temporarily_unavailable" or "authorization_pending" or "slow_down" or "expired_token"
                    or "unsupported_response_type" or "authentication_error" or "permission_error"
                    or "rate_limit_error" or "invalid_request_error" or "not_found_error" or "overloaded_error"
                    or "api_error" => code,
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadNestedCode(JsonElement error) =>
        error.ValueKind == JsonValueKind.Object
        && error.TryGetProperty("type", out var type)
        && type.ValueKind == JsonValueKind.String
            ? type.GetString()
            : null;
}
