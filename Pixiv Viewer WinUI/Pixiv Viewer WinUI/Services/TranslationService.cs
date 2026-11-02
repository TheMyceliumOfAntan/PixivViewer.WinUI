using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Pixiv_Viewer_WinUI.Services;

/// <summary>
/// 文本翻译（Pixiv 作品说明 / 评论）。
///
/// 主引擎：B 站 Index-Translate 免费公开 API（OpenAI 兼容，无需 key）
///         POST https://index-translate.bilibili.com/v1/chat/completions
/// 兜底：  MyMemory（免费、免 key，单次 500 字节，长文本按行切块）
///
/// 结果按「源语言|目标语言|原文」缓存，来回切换「翻译 / 原文」不会重复请求。
/// </summary>
public static class TranslationService
{
    private const string IndexTranslateEndpoint = "https://index-translate.bilibili.com/v1/chat/completions";
    private const string IndexTranslateModel = "Index-Translate-35B-A3B";

    private const string MyMemoryEndpoint = "https://api.mymemory.translated.net/get";

    /// <summary>Index-Translate 一次请求的输入上限（留余量，输出 1024 token 够用）。</summary>
    private const int IndexChunkChars = 1200;

    /// <summary>MyMemory 单次请求的 q 参数上限是 500 字节，留一点余量。</summary>
    private const int MyMemoryChunkBytes = 450;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(40) };

    private static readonly ConcurrentDictionary<string, string> Cache = new();

    static TranslationService()
    {
        // Index-Translate 的网关有 WAF，缺 UA 会被拦
        Http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Index-Translate-Client/1.0");
    }

    /// <summary>最近一次翻译实际使用的引擎（用于提示/诊断）。</summary>
    public static string LastEngine { get; private set; } = string.Empty;

    /// <summary>翻译一段文本；两个引擎都失败时返回 null（调用方回退到原文）。</summary>
    public static async Task<string?> TranslateAsync(
        string? text,
        string from = "ja",
        string to = "zh-CN",
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var key = $"{from}|{to}|{text}";
        if (Cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var translated = await TranslateWithIndexAsync(text, to, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(translated))
        {
            LastEngine = "Index-Translate";
            Cache[key] = translated;
            return translated;
        }

        translated = await TranslateWithMyMemoryAsync(text, from, to, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(translated))
        {
            LastEngine = "MyMemory";
            Cache[key] = translated;
            return translated;
        }

        return null;
    }

    // ---------------- Index-Translate ----------------

    private static async Task<string?> TranslateWithIndexAsync(
        string text,
        string to,
        CancellationToken cancellationToken)
    {
        var chunks = SplitByLength(text, IndexChunkChars);
        var builder = new StringBuilder(text.Length);

        foreach (var chunk in chunks)
        {
            var translated = await TranslateIndexChunkAsync(chunk, to, cancellationToken).ConfigureAwait(false);
            if (translated is null)
            {
                // 任何一块失败都整体回退，避免半中半日
                return null;
            }

            if (builder.Length > 0 && !translated.StartsWith('\n'))
            {
                builder.Append('\n');
            }

            builder.Append(translated);
        }

        return builder.ToString().Trim();
    }

    private static async Task<string?> TranslateIndexChunkAsync(
        string chunk,
        string to,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(chunk))
        {
            return chunk;
        }

        try
        {
            var target = to.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "简体中文" : to;
            var prompt =
                $"请将以下文本翻译为{target}，直接输出翻译结果，不要进行任何解释。\n\n{chunk}";

            var payload = new
            {
                model = IndexTranslateModel,
                messages = new[] { new { role = "user", content = prompt } },
                temperature = 0,
            };

            using var response = await Http
                .PostAsJsonAsync(IndexTranslateEndpoint, payload, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);

            if (!document.RootElement.TryGetProperty("choices", out var choices) ||
                choices.GetArrayLength() == 0 ||
                !choices[0].TryGetProperty("message", out var message) ||
                !message.TryGetProperty("content", out var content))
            {
                return null;
            }

            var result = content.GetString();
            return string.IsNullOrWhiteSpace(result) ? null : result.Trim();
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ---------------- MyMemory（兜底） ----------------

    private static async Task<string?> TranslateWithMyMemoryAsync(
        string text,
        string from,
        string to,
        CancellationToken cancellationToken)
    {
        var chunks = SplitIntoChunks(text, MyMemoryChunkBytes);
        var builder = new StringBuilder(text.Length);

        foreach (var chunk in chunks)
        {
            var translated = await TranslateMyMemoryChunkAsync(chunk, from, to, cancellationToken).ConfigureAwait(false);
            if (translated is null)
            {
                return null;
            }

            builder.Append(translated);
        }

        return builder.ToString();
    }

    private static async Task<string?> TranslateMyMemoryChunkAsync(
        string chunk,
        string from,
        string to,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(chunk))
        {
            return chunk;
        }

        try
        {
            var url = $"{MyMemoryEndpoint}?q={Uri.EscapeDataString(chunk)}&langpair={from}|{to}";
            using var response = await Http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.TryGetProperty("responseStatus", out var status) &&
                status.ValueKind == JsonValueKind.Number &&
                status.GetInt32() != 200)
            {
                return null;
            }

            if (!root.TryGetProperty("responseData", out var data) ||
                !data.TryGetProperty("translatedText", out var translatedText))
            {
                return null;
            }

            var result = translatedText.GetString();
            return string.IsNullOrWhiteSpace(result) ? null : result;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ---------------- 切块 ----------------

    /// <summary>按行聚合切块（按字符数），用于 Index-Translate。</summary>
    private static List<string> SplitByLength(string text, int maxChars)
    {
        var chunks = new List<string>();
        var current = new StringBuilder();

        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (current.Length > 0 && current.Length + line.Length + 1 > maxChars)
            {
                chunks.Add(current.ToString());
                current.Clear();
            }

            if (line.Length > maxChars)
            {
                for (var offset = 0; offset < line.Length; offset += maxChars)
                {
                    chunks.Add(line.Substring(offset, Math.Min(maxChars, line.Length - offset)));
                }

                continue;
            }

            if (current.Length > 0)
            {
                current.Append('\n');
            }

            current.Append(line);
        }

        if (current.Length > 0)
        {
            chunks.Add(current.ToString());
        }

        return chunks;
    }

    /// <summary>按行聚合切块，保证每块不超过 maxBytes（UTF-8），用于 MyMemory。</summary>
    private static List<string> SplitIntoChunks(string text, int maxBytes)
    {
        var chunks = new List<string>();
        var current = new StringBuilder();
        var currentBytes = 0;

        foreach (var rawLine in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine + "\n";
            var lineBytes = Encoding.UTF8.GetByteCount(line);

            if (currentBytes > 0 && currentBytes + lineBytes > maxBytes)
            {
                chunks.Add(current.ToString().TrimEnd('\n'));
                current.Clear();
                currentBytes = 0;
            }

            // 单行本身超长时按字符硬切
            if (lineBytes > maxBytes)
            {
                var piece = new StringBuilder();
                var pieceBytes = 0;
                foreach (var ch in line)
                {
                    var size = Encoding.UTF8.GetByteCount(ch.ToString());
                    if (pieceBytes + size > maxBytes)
                    {
                        chunks.Add(piece.ToString());
                        piece.Clear();
                        pieceBytes = 0;
                    }

                    piece.Append(ch);
                    pieceBytes += size;
                }

                current.Append(piece);
                currentBytes += pieceBytes;
                continue;
            }

            current.Append(line);
            currentBytes += lineBytes;
        }

        if (current.Length > 0)
        {
            chunks.Add(current.ToString().TrimEnd('\n'));
        }

        return chunks;
    }
}
