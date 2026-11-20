using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace Hgvd.Xqd;

/// <summary>
/// JsonElement 取值助手 —— 网关字段类型不固定（数字可能是字符串），统一容错。
/// 与上游 `Xqd.Core.J` 语义一致。
/// </summary>
public static class J
{
    public static JsonElement Parse(string text) => JsonDocument.Parse(text).RootElement.Clone();

    public static JsonElement Get(this JsonElement e, string key)
    {
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v)) return v;
        return default;
    }

    public static string Str(this JsonElement e, string key, string def = "")
        => StrSelf(e.Get(key), def);

    public static string StrSelf(this JsonElement v, string def = "")
    {
        if (v.ValueKind == JsonValueKind.String) return v.GetString() ?? def;
        if (v.ValueKind == JsonValueKind.Number) return v.GetRawText();
        if (v.ValueKind == JsonValueKind.True) return "true";
        if (v.ValueKind == JsonValueKind.False) return "false";
        return def;
    }

    public static long Long(this JsonElement e, string key, long def = 0)
    {
        var v = e.Get(key);
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String &&
            long.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var m))
            return m;
        return def;
    }

    public static double Dbl(this JsonElement e, string key, double def = 0)
    {
        var v = e.Get(key);
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String &&
            double.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var m))
            return m;
        return def;
    }

    public static bool Bool(this JsonElement e, string key, bool def = false)
    {
        var v = e.Get(key);
        if (v.ValueKind == JsonValueKind.True) return true;
        if (v.ValueKind == JsonValueKind.False) return false;
        return def;
    }

    public static List<JsonElement> Arr(this JsonElement e, string key)
    {
        var list = new List<JsonElement>();
        var v = e.Get(key);
        if (v.ValueKind == JsonValueKind.Array)
            foreach (var x in v.EnumerateArray()) list.Add(x);
        return list;
    }
}
