using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Pinshot.Core;

/// <summary>
/// 翻译服务。默认走免费接口链（有道 → MyMemory → Google），无需任何配置；
/// 用户在设置中填写 OpenAI 兼容接口或 DeepL 密钥后，则优先使用该接口。
/// </summary>
public static class TranslateService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static async Task<string> TranslateAsync(string text, Config config)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("没有可翻译的文字。");

        // 命令行/代码/URL：整句没有可译的自然语言（引擎只会原样吐回），
        // 但里面的英文单词按词典逐个查出，生成「词=译」对照表，方便看懂单词。
        if (IsLikelyCode(text))
            return await TranslateAsGlossaryAsync(text, config) ?? text.Trim();

        // 中英混合文本：按语言分段，只翻译非中文段，中文段原样保留（默认目标=中文简体）。
        // 直接整句送 auto 翻译时，混合句里的英文常被漏翻或译错（实测痛点）。
        // 目标语言跟随设置（默认 zh-CN）；设置为目标外文时才反向处理。
        var target = (config.TranslateProvider.ToLowerInvariant() switch
        {
            "openai" => config.OpenAI.TargetLang,
            "deepl" => config.DeepL.TargetLang,
            _ => config.FreeTargetLang,
        }).ToLowerInvariant();
        var wantsChinese = target.StartsWith("zh");
        if (wantsChinese || target.StartsWith("en"))
        {
            var segments = SplitByScript(text, wantsChinese);
            // 整行没有任何需要翻译的外语字段（如目标=中文、整行全是中文）：
            // 直接原样返回。否则“结果==原文”会被误判为未翻译，
            // 该行被判失败，图上翻译就不覆盖（表现为“有些行不翻译”）。
            if (segments.Parts.Count > 0 && segments.Parts.All(p => p.Keep))
                return text.Trim();
            if (segments.Count > 1)
            {
                var translatedParts = new List<string>();
                foreach (var segment in segments.Parts)
                {
                    if (segment.Keep)
                    {
                        translatedParts.Add(segment.Text);
                        continue;
                    }
                    // 非中文段 → 统一译成中文（segmentConfig 目标已设 zh-CN）
                    var segmentConfig = System.Text.Json.JsonSerializer.Deserialize<Config>(
                        System.Text.Json.JsonSerializer.Serialize(config))!;
                    segmentConfig.FreeTargetLang = "zh-CN";
                    try
                    {
                        translatedParts.Add(await TranslateSingleAsync(segment.Text, segmentConfig));
                    }
                    catch
                    {
                        // 整段翻不出（多为命令/代码片段，引擎只会原样吐回）：
                        // 转逐词对照词表；连单词都查不到（专有标识符）则保留原文
                        var glossary = await TranslateAsGlossaryAsync(segment.Text, segmentConfig);
                        translatedParts.Add(glossary ?? segment.Text);
                    }
                }
                return string.Join("", translatedParts).Trim();
            }
        }
        else if (IsSameScriptAsTarget(text, target))
        {
            // 其他目标语言（日/韩/俄等）：原文主文字已是目标语言，无需送引擎
            return text.Trim();
        }

        return await TranslateSingleAsync(text, config);
    }

    /// <summary>
    /// 非中英目标：原文的主文字与目标语言一致（无需翻译）时 true。
    /// 假名是日语的独有标志；谚文/西里尔/拉丁按占比过半且无其他文字判断。
    /// </summary>
    private static bool IsSameScriptAsTarget(string text, string target)
    {
        var kana = 0;
        var hangul = 0;
        var cyrillic = 0;
        var latin = 0;
        var total = 0;
        foreach (var ch in text)
        {
            if (!char.IsLetter(ch))
                continue;
            total++;
            if (ch >= 0x3040 && ch <= 0x30FF) kana++;
            else if (ch >= 0xAC00 && ch <= 0xD7AF) hangul++;
            else if (ch >= 0x0400 && ch <= 0x04FF) cyrillic++;
            else if ((ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z')) latin++;
        }
        if (total == 0)
            return false;
        return target switch
        {
            "ja" => kana > 0,
            "ko" => hangul * 2 >= total,
            "ru" => cyrillic * 2 >= total,
            _ => latin * 2 >= total && kana == 0 && hangul == 0 && cyrillic == 0,
        };
    }

    /// <summary>
    /// 疑似命令行/代码/路径/URL：不含中日韩文字、字母 ≥ 4 且符号数 ≥ 字母数的一半，
    /// 或包含 "://"。这类文本整句没有可译的自然语言，转逐词对照。
    /// </summary>
    private static bool IsLikelyCode(string text)
    {
        var letters = 0;
        var symbols = 0;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
                continue;
            if (char.IsLetter(ch))
            {
                // 含中日韩文字 → 按自然语言处理（代码判定只针对纯西文内容）
                if ((ch >= 0x2E80 && ch <= 0x9FFF) || (ch >= 0xAC00 && ch <= 0xD7AF))
                    return false;
                letters++;
            }
            else if (!char.IsDigit(ch))
                symbols++;
        }
        return letters >= 4 && (symbols * 2 >= letters || text.Contains("://"));
    }

    /// <summary>
    /// 代码/命令逐词对照：提取单词（驼峰命名自动拆词），逐个查词典，
    /// 生成「词=译」对照表。原命令本身不改写（改写只会破坏命令）。
    /// 一个可译的词都没有时返回 null（调用方保留原文）。
    /// </summary>
    private static async Task<string?> TranslateAsGlossaryAsync(string text, Config config)
    {
        var words = ExtractCodeWords(text);
        if (words.Count == 0)
            return null;

        var results = new string?[words.Count];
        using var semaphore = new SemaphoreSlim(4);
        var tasks = Enumerable.Range(0, words.Count).Select(async i =>
        {
            await semaphore.WaitAsync();
            try
            {
                var meaning = (await TranslateSingleAsync(words[i], config)).Trim().TrimEnd('。', '.');
                if (!string.IsNullOrWhiteSpace(meaning) &&
                    !string.Equals(meaning, words[i], StringComparison.OrdinalIgnoreCase))
                    results[i] = words[i] + "=" + meaning;
            }
            catch
            {
                // 查不到的词（专有名词/标识符）跳过
            }
            finally
            {
                semaphore.Release();
            }
        }).ToArray();
        await Task.WhenAll(tasks);

        var glossary = string.Join("  ", results.Where(r => !string.IsNullOrWhiteSpace(r)));
        return glossary.Length > 0 ? glossary : null;
    }

    /// <summary>提取代码文本中的单词：≥3 字母的词，驼峰命名拆分（ApplicationService→Application/Service），去重，最多 16 个。</summary>
    private static List<string> ExtractCodeWords(string text)
    {
        var words = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(text, "[A-Za-z]{3,}"))
        {
            foreach (var part in SplitCamel(m.Value))
            {
                if (part.Length < 3 || !seen.Add(part.ToLowerInvariant()))
                    continue;
                words.Add(part);
                if (words.Count >= 16)
                    return words;
            }
        }
        return words;
    }

    private static IEnumerable<string> SplitCamel(string token)
    {
        var start = 0;
        for (var i = 1; i < token.Length; i++)
        {
            if (char.IsUpper(token[i]) && char.IsLower(token[i - 1]))
            {
                yield return token[start..i];
                start = i;
            }
        }
        yield return token[start..];
    }

    /// <summary>单段文本翻译（免费链自动回退 + 用户配置接口优先）。</summary>
    private static async Task<string> TranslateSingleAsync(string text, Config config)
    {
        var errors = new List<string>();
        var provider = (config.TranslateProvider ?? "auto").Trim().ToLowerInvariant();

        // 1) 用户显式配置的接口优先
        if (provider == "openai" && !string.IsNullOrWhiteSpace(config.OpenAI.ApiKey))
        {
            try { return await TranslateByOpenAiAsync(text, config.OpenAI); }
            catch (Exception ex) { errors.Add($"OpenAI：{ex.Message}"); }
        }
        else if (provider == "deepl" && !string.IsNullOrWhiteSpace(config.DeepL.AuthKey))
        {
            try { return await TranslateByDeepLAsync(text, config.DeepL); }
            catch (Exception ex) { errors.Add($"DeepL：{ex.Message}"); }
        }

        // 2) 免费接口链自动回退（逐个尝试，任一成功即返回）
        //    返回结果与原文相同（含字母时）视为“未翻译”，继续尝试下一接口
        var target = NormalizeTarget(config.FreeTargetLang);
        // 按用户勾选的引擎顺序组链（设置 → 翻译与提取文字 → 引擎勾选）
        var engines = (config.FreeEngines is { Count: > 0 } chosen
                ? chosen
                : ["youdao", "mymemory", "google"])
            .Select(e => e.Trim().ToLowerInvariant())
            .Distinct()
            .ToList();
        var allEngines = new Dictionary<string, Func<string, string, Task<string>>>(StringComparer.OrdinalIgnoreCase)
        {
            ["youdao"] = TranslateByYoudaoAsync,
            ["mymemory"] = TranslateByMyMemoryAsync,
            ["google"] = TranslateByGoogleAsync,
            ["baidu"] = (text, to) => TranslateByBaiduAsync(text, to, config),
            ["custom"] = (text, to) => TranslateByCustomAsync(text, to, config),
        };
        foreach (var engineName in engines)
        {
            if (!allEngines.TryGetValue(engineName, out var translate))
                continue;
            var name = engineName switch
            {
                "youdao" => "有道",
                "mymemory" => "MyMemory",
                "google" => "Google",
                "baidu" => "百度",
                "custom" => "自定义接口",
                _ => engineName,
            };
            try
            {
                var result = await translate(text, target);
                if (IsUntranslated(result, text))
                {
                    errors.Add($"{name}：返回原文");
                    continue;
                }
                return result;
            }
            catch (Exception ex)
            {
                errors.Add($"{name}：{ex.Message}");
            }
        }

        throw new InvalidOperationException("所有翻译接口均不可用。" + string.Join("；", errors));
    }

    /// <summary>结果与原文一致（原样返回缩写/单词）时判定为未翻译。</summary>
    private static bool IsUntranslated(string result, string source)
    {
        if (string.IsNullOrWhiteSpace(result))
            return true;
        if (!source.Any(char.IsLetter))
            return false;
        return string.Equals(result.Trim(), source.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    #region 免费接口

    /// <summary>有道翻译演示接口（无需密钥，中文质量较好）。</summary>
    private static async Task<string> TranslateByYoudaoAsync(string text, string target)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["q"] = text,
            ["from"] = "auto",
            ["to"] = target == "zh-CN" ? "zh-CHS" : target == "zh-TW" ? "zh-CHT" : target,
        });
        using var response = await Http.PostAsync("https://aidemo.youdao.com/trans", content);
        var json = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode}");

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("errorCode", out var code) &&
            code.ValueKind == JsonValueKind.String && code.GetString() != "0")
            throw new InvalidOperationException($"错误码 {code.GetString()}");

        if (doc.RootElement.TryGetProperty("translation", out var translation) &&
            translation.ValueKind == JsonValueKind.Array && translation.GetArrayLength() > 0)
        {
            var result = string.Concat(translation.EnumerateArray().Select(t => t.GetString()));
            if (!string.IsNullOrWhiteSpace(result))
                return result;
        }
        throw new InvalidOperationException("返回内容为空");
    }

    /// <summary>MyMemory 免费接口（无需密钥）。</summary>
    private static async Task<string> TranslateByMyMemoryAsync(string text, string target)
    {
        var source = GuessSourceLanguage(text);
        var url = "https://api.mymemory.translated.net/get" +
                  $"?q={Uri.EscapeDataString(text)}&langpair={Uri.EscapeDataString(source)}|{Uri.EscapeDataString(target)}";
        using var response = await Http.GetAsync(url);
        var json = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode}");

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("responseData", out var data) &&
            data.TryGetProperty("translatedText", out var translated))
        {
            var result = translated.GetString();
            if (!string.IsNullOrWhiteSpace(result) && !result.Contains("MYMEMORY WARNING", StringComparison.OrdinalIgnoreCase))
                return result;
        }
        throw new InvalidOperationException("返回内容为空");
    }

    /// <summary>Google 免费网页接口（部分地区不可达，作为兜底）。</summary>
    private static async Task<string> TranslateByGoogleAsync(string text, string target)
    {
        var url = "https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&dt=t" +
                  $"&tl={Uri.EscapeDataString(target)}&q={Uri.EscapeDataString(text)}";
        using var response = await Http.GetAsync(url);
        var json = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode}");

        using var doc = JsonDocument.Parse(json);
        var builder = new StringBuilder();
        if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
        {
            foreach (var segment in doc.RootElement[0].EnumerateArray())
            {
                if (segment.ValueKind == JsonValueKind.Array && segment.GetArrayLength() > 0 &&
                    segment[0].ValueKind == JsonValueKind.String)
                    builder.Append(segment[0].GetString());
            }
        }
        var result = builder.ToString();
        if (string.IsNullOrEmpty(result))
            throw new InvalidOperationException("返回内容为空");
        return result;
    }

    /// <summary>粗略判断源语言（MyMemory 需要显式源语言）。</summary>
    private static string GuessSourceLanguage(string text)
    {
        foreach (var ch in text)
        {
            if (ch >= 0x3040 && ch <= 0x30FF) return "ja";       // 假名
            if (ch >= 0xAC00 && ch <= 0xD7AF) return "ko";       // 谚文
            if (ch >= 0x0400 && ch <= 0x04FF) return "ru";       // 西里尔
            if (ch >= 0x4E00 && ch <= 0x9FFF) return "zh-CN";    // 汉字
        }
        return "en";
    }

    private static string NormalizeTarget(string? target) =>
        string.IsNullOrWhiteSpace(target) ? "zh-CN" : target.Trim();

    #endregion

    #region 百度 / 自定义接口

    /// <summary>百度翻译开放平台（免费额度，需 APPID + 密钥）。</summary>
    private static async Task<string> TranslateByBaiduAsync(string text, string to, Config config)
    {
        if (string.IsNullOrWhiteSpace(config.BaiduAppId) || string.IsNullOrWhiteSpace(config.BaiduSecret))
            throw new InvalidOperationException("未配置百度 APPID/密钥");
        var toCode = to == "zh-CN" ? "zh" : to == "zh-TW" ? "zh" : to == "en" ? "en" : to == "ja" ? "jp" : to == "ko" ? "kor" : to == "ru" ? "ru" : to;
        var salt = Random.Shared.Next(1, int.MaxValue).ToString();
        var sign = Md5(config.BaiduAppId + text + salt + config.BaiduSecret);
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["q"] = text, ["from"] = "auto", ["to"] = toCode,
            ["appid"] = config.BaiduAppId, ["salt"] = salt, ["sign"] = sign,
        });
        using var response = await Http.PostAsync("https://fanyi-api.baidu.com/api/trans/vip/translate", content);
        var json = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode}");

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("error_code", out var err))
            throw new InvalidOperationException($"错误码 {err.GetString()}");
        if (doc.RootElement.TryGetProperty("trans_result", out var list) && list.GetArrayLength() > 0)
        {
            var builder = new StringBuilder();
            foreach (var item in list.EnumerateArray())
                if (item.TryGetProperty("dst", out var dst))
                    builder.Append(dst.GetString());
            var result = builder.ToString();
            if (!string.IsNullOrWhiteSpace(result))
                return result;
        }
        throw new InvalidOperationException("返回内容为空");
    }

    /// <summary>
    /// 自定义 GET 接口：URL 模板支持 {text} {to} {from} 占位符；
    /// 响应依次尝试常见 JSON 字段（data / translation / result / text / content），失败则按纯文本处理。
    /// </summary>
    private static async Task<string> TranslateByCustomAsync(string text, string to, Config config)
    {
        var url = config.CustomUrl;
        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException("未配置自定义接口地址");
        url = url.Replace("{text}", Uri.EscapeDataString(text))
                 .Replace("{to}", Uri.EscapeDataString(to))
                 .Replace("{from}", "auto");
        using var response = await Http.GetAsync(url);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode}");

        // JSON 常见字段依次探测，都没有则按纯文本
        try
        {
            using var doc = JsonDocument.Parse(body);
            foreach (var path in new[] { "data", "translation", "result", "text", "content" })
            {
                if (!doc.RootElement.TryGetProperty(path, out var node))
                    continue;
                if (node.ValueKind == JsonValueKind.String)
                    return node.GetString() ?? "";
                if (node.ValueKind == JsonValueKind.Array && node.GetArrayLength() > 0)
                {
                    var first = node[0];
                    if (first.ValueKind == JsonValueKind.String)
                        return first.GetString() ?? "";
                    if (first.ValueKind == JsonValueKind.Array && first.GetArrayLength() > 0 &&
                        first[0].ValueKind == JsonValueKind.String)
                        return first[0].GetString() ?? "";
                }
            }
        }
        catch (JsonException)
        {
            // 非 JSON → 纯文本
        }
        var plain = body.Trim();
        return string.IsNullOrWhiteSpace(plain)
            ? throw new InvalidOperationException("返回内容为空")
            : plain;
    }

    private static string Md5(string input)
    {
        var bytes = System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    #endregion

    #region 用户配置接口

    private static async Task<string> TranslateByOpenAiAsync(string text, Config.OpenAiConfig o)
    {
        var baseUrl = o.BaseUrl.TrimEnd('/');
        var body = JsonSerializer.Serialize(new
        {
            model = o.Model,
            temperature = 0.1,
            messages = new object[]
            {
                new { role = "system", content = $"You are a translation engine. Translate the user's text into {o.TargetLang}. Output ONLY the translation, without any explanation or quotes." },
                new { role = "user", content = text }
            }
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/chat/completions");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", o.ApiKey);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using var response = await Http.SendAsync(request);
        var json = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode}：{Truncate(json, 200)}");

        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content")
            .GetString()?.Trim() ?? throw new InvalidOperationException("返回内容为空");
    }

    private static async Task<string> TranslateByDeepLAsync(string text, Config.DeepLConfig d)
    {
        var host = d.UseFreeApi ? "https://api-free.deepl.com" : "https://api.deepl.com";
        using var response = await Http.PostAsync($"{host}/v2/translate",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["auth_key"] = d.AuthKey,
                ["text"] = text,
                ["target_lang"] = d.TargetLang,
            }));
        var json = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode}：{Truncate(json, 200)}");

        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("translations")[0].GetProperty("text").GetString()
            ?? throw new InvalidOperationException("返回内容为空");
    }

    #endregion

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
    /// <summary>
    /// 中英混合文本分段：目标为中文时，拉丁字母词段标记为“需翻译”、中文段保留；
    /// 目标为英文时相反。数字与标点跟随所在段。
    /// </summary>
    internal static (List<(string Text, bool Keep)> Parts, int Count) SplitByScript(
        string text, bool targetChinese)
    {
        static bool IsChinese(char ch) => ch >= 0x4E00 && ch <= 0x9FFF;
        static bool IsAlpha(char ch) => (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z');

        var parts = new List<(string Text, bool Keep)>();
        var buffer = new System.Text.StringBuilder();
        var bufferKeep = true;

        void Flush()
        {
            if (buffer.Length == 0)
                return;
            parts.Add((buffer.ToString(), bufferKeep));
            buffer.Clear();
        }

        foreach (var ch in text)
        {
            bool keep;
            if (targetChinese)
                keep = IsChinese(ch) || !IsAlpha(ch);      // 中文/数字/标点保留，字母翻译
            else
                keep = !IsChinese(ch);                     // 非中文保留，中文翻译

            // 起始段先按首字符定调
            if (buffer.Length == 0)
                bufferKeep = keep;
            else if (keep != bufferKeep && char.IsLetter(ch))
            {
                Flush();
                bufferKeep = keep;
            }
            buffer.Append(ch);
        }
        Flush();
        return (parts, parts.Count);
    }
}
