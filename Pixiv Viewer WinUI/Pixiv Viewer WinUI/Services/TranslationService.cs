using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
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

    /// <summary>
    /// 批量翻译多行文字（漫画一页的所有对白）。
    /// 一次请求带编号，比逐条请求快且能保留上下文；解析失败自动回退到逐条翻译。
    /// </summary>
    public static async Task<List<string?>> TranslateLinesAsync(
        IReadOnlyList<string> lines,
        string from = "ja",
        string to = "zh-CN",
        CancellationToken cancellationToken = default)
    {
        var results = new List<string?>(lines.Count);
        var pending = new List<(int Index, string Text)>();

        for (var i = 0; i < lines.Count; i++)
        {
            var text = lines[i];
            if (string.IsNullOrWhiteSpace(text))
            {
                results.Add(string.Empty);
                continue;
            }

            if (Cache.TryGetValue($"{from}|{to}|{text}", out var cached))
            {
                results.Add(cached);
                continue;
            }

            results.Add(null);
            pending.Add((i, text));
        }

        if (pending.Count == 0)
        {
            return results;
        }

        // 单行或总量很小：直接走单条翻译，避免编号解析出错
        if (pending.Count == 1 || pending.Sum(p => p.Text.Length) < 40)
        {
            foreach (var (index, text) in pending)
            {
                results[index] = await TranslateAsync(text, from, to, cancellationToken).ConfigureAwait(false);
            }

            return results;
        }

        var chunks = pending.Chunk(8).Select(chunk => chunk.ToList()).ToList();
        var gate = new SemaphoreSlim(4);
        var translatedChunks = await Task.WhenAll(chunks.Select(async chunk =>
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    return await TranslateBatchAsync(chunk, to, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    gate.Release();
                }
            }))
            .ConfigureAwait(false);

        for (var i = 0; i < chunks.Count; i++)
        {
            // 行列数对不上说明模型合并/漏行了，这一块全部交给逐条重译，避免整段错位
            var aligned = translatedChunks[i].Count == chunks[i].Count;
            for (var j = 0; j < chunks[i].Count; j++)
            {
                var value = aligned ? translatedChunks[i][j] : null;
                results[chunks[i][j].Index] = value;

                if (!string.IsNullOrWhiteSpace(value))
                {
                    Cache[$"{from}|{to}|{chunks[i][j].Text}"] = value!;
                }
            }
        }

        // 批量里没翻出来的，再逐条补一次（限并发 4，避免被接口限流）
        var retryGate = new SemaphoreSlim(6);
        var retries = pending
            .Where(p => string.IsNullOrWhiteSpace(results[p.Index]))
            .Select(async p =>
            {
                await retryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    results[p.Index] = await TranslateAsync(p.Text, from, to, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    retryGate.Release();
                }
            });

        await Task.WhenAll(retries).ConfigureAwait(false);

        return results;
    }

    /// <summary>最近一次批量翻译的原始返回（诊断用，null 表示请求失败）。</summary>
    public static string LastBatchInfo { get; private set; } = string.Empty;

    private static async Task<List<string?>> TranslateBatchAsync(
        List<(int Index, string Text)> pending,
        string to,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        builder.AppendLine("以下是从漫画中识别出的日文对白，每行以「序号.」开头。请把每一行翻译成简体中文，");
        builder.AppendLine("保持相同的序号和行数，只输出译文，不要解释，不要合并或拆分行。");
        builder.AppendLine();

        for (var i = 0; i < pending.Count; i++)
        {
            // 对白里可能本来就有换行，压成一行避免破坏序号
            var text = pending[i].Text.Replace("\r", " ").Replace("\n", " ").Trim();
            builder.Append(i + 1).Append(". ").AppendLine(text);
        }

        var answer = await TranslateRawAsync(builder.ToString(), to, cancellationToken).ConfigureAwait(false);
        LastBatchInfo = answer is null ? "null" : answer[..Math.Min(300, answer.Length)].Replace("\n", " | ");
        var result = new List<string?>(new string?[pending.Count]);
        if (string.IsNullOrWhiteSpace(answer))
        {
            return result;
        }

        var lines = answer.Replace("\r\n", "\n").Split('\n');
        var cursor = 0;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            // 去掉「1.」「1、」「1)」这类前缀
            var dot = line.IndexOfAny(new[] { '.', '、', ')', '．', '。' });
            if (dot > 0 && dot <= 4 && int.TryParse(line[..dot], out var number) && number == cursor + 1)
            {
                line = line[(dot + 1)..].Trim();
            }
            else if (int.TryParse(line.Split(' ')[0].TrimEnd('.', '、', ')'), out var sequential) &&
                     sequential == cursor + 1)
            {
                line = line[(line.Split(' ')[0].Length)..].Trim();
            }

            if (line.Length == 0)
            {
                continue;
            }

            if (cursor < result.Count)
            {
                result[cursor] = line;
            }

            cursor++;
        }

        return result;
    }

    /// <summary>原样发送一段提示词，返回模型输出（批量翻译用）。单次请求 15 秒超时。</summary>
    private static async Task<string?> TranslateRawAsync(string prompt, string to, CancellationToken cancellationToken)
    {
        var target = to.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "简体中文" : to;
        var payload = new
        {
            model = IndexTranslateModel,
            messages = new[]
            {
                new { role = "system", content = $"你是一名漫画翻译，只把日文翻译成{target}，严格按输入行数输出。" },
                new { role = "user", content = prompt },
            },
            temperature = 0,
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));

        try
        {
            using var response = await Http
                .PostAsJsonAsync(IndexTranslateEndpoint, payload, timeout.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("choices", out var choices) ||
                choices.GetArrayLength() == 0 ||
                !choices[0].TryGetProperty("message", out var message) ||
                !message.TryGetProperty("content", out var content))
            {
                return null;
            }

            return content.GetString()?.Trim();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 单次请求超时：交给上层回退逐条翻译，不中断整页
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
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
