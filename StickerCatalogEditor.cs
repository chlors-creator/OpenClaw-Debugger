using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OpenClawDebugger;

public sealed class ParsedStickerCatalog
{
    private readonly JsonNode _root;
    private readonly List<(StickerRow Row, JsonObject Item, string? TagProperty)> _bindings;
    private readonly JsonSerializerOptions _options = new() { WriteIndented = true };

    internal ParsedStickerCatalog(JsonNode root, List<(StickerRow, JsonObject, string?)> bindings)
    {
        _root = root;
        _bindings = bindings;
    }

    public IReadOnlyList<StickerRow> Rows => _bindings.Select(x => x.Row).ToArray();

    public string SerializeWithEdits()
    {
        foreach (var (row, item, existingProperty) in _bindings)
        {
            var key = existingProperty ?? "tags";
            var tags = row.TagsText.Split([',', '，', ';', '；', '\n', '\r'],
                StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (item[key] is JsonArray)
                item[key] = new JsonArray(tags.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
            else
                item[key] = string.Join(", ", tags);
        }

        return _root.ToJsonString(_options) + Environment.NewLine;
    }
}

public static class StickerCatalogEditor
{
    private static readonly string[] ArrayNames = ["stickers", "items", "entries", "images", "catalog"];
    private static readonly string[] IdNames = ["id", "number", "index", "no", "编号"];
    private static readonly string[] ImageNames = ["file", "filename", "image", "path", "src", "asset", "name"];
    private static readonly string[] TagNames = ["tags", "labels", "keywords", "标签"];

    public static bool TryParse(string json, IReadOnlyList<RemoteFile> images,
        out ParsedStickerCatalog? catalog, out string error)
    {
        catalog = null;
        error = "";
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (JsonException ex)
        {
            error = $"catalog.json 不是有效 JSON：{ex.Message}";
            return false;
        }

        if (root is null)
        {
            error = "catalog.json 内容为空。";
            return false;
        }
        var array = FindEntryArray(root);
        if (array is null)
        {
            error = "暂不识别 catalog.json 的目录结构。文件保持只读，避免破坏现有格式。";
            return false;
        }

        var bindings = new List<(StickerRow, JsonObject, string?)>();
        var imageNames = images.Where(x => x.IsImage).Select(x => x.RelativePath).ToArray();
        foreach (var node in array)
        {
            if (node is not JsonObject item) continue;
            var idProp = FindProperty(item, IdNames);
            var imageProp = FindProperty(item, ImageNames);
            var tagProp = FindProperty(item, TagNames);
            if (tagProp is null || (item[tagProp] is not JsonArray && item[tagProp] is not JsonValue))
            {
                error = "目录条目没有可安全识别的字符串标签字段。请使用高级原始文件编辑，结构化标签编辑保持关闭。";
                return false;
            }
            if (item[tagProp] is JsonArray existingTags && existingTags.Any(tag => tag is not null && tag is not JsonValue))
            {
                error = "标签数组包含非文本结构。请使用高级原始文件编辑，结构化标签编辑保持关闭。";
                return false;
            }
            var id = idProp is null ? "" : ScalarText(item[idProp]);
            var imagePath = imageProp is null ? "" : ScalarText(item[imageProp]);
            if (string.IsNullOrWhiteSpace(imagePath))
                imagePath = FindImageById(imageNames, id);
            if (string.IsNullOrWhiteSpace(id))
                id = Path.GetFileNameWithoutExtension(imagePath);
            if (string.IsNullOrWhiteSpace(id) && string.IsNullOrWhiteSpace(imagePath))
                continue;

            var tags = tagProp is null ? [] : ParseTags(item[tagProp]);
            var row = new StickerRow
            {
                Id = id,
                ImagePath = imagePath,
                TagsText = string.Join(", ", tags)
            };
            bindings.Add((row, item, tagProp));
        }

        if (bindings.Count == 0)
        {
            error = "catalog.json 中没有识别到表情包条目。";
            return false;
        }

        catalog = new ParsedStickerCatalog(root, bindings);
        return true;
    }

    private static JsonArray? FindEntryArray(JsonNode root)
    {
        if (root is JsonArray topArray) return topArray;
        if (root is not JsonObject obj) return null;
        foreach (var name in ArrayNames)
            if (TryGetProperty(obj, name, out var node) && node is JsonArray arr) return arr;
        foreach (var pair in obj)
            if (pair.Value is JsonObject nested && FindEntryArray(nested) is { } found) return found;
        return null;
    }

    private static string? FindProperty(JsonObject obj, IEnumerable<string> names)
    {
        foreach (var name in names)
            foreach (var key in obj.Select(pair => pair.Key))
                if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                    return key;
        return null;
    }

    private static bool TryGetProperty(JsonObject obj, string name, out JsonNode? value)
    {
        foreach (var pair in obj)
        {
            if (!string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)) continue;
            value = pair.Value;
            return true;
        }
        value = null;
        return false;
    }

    private static string ScalarText(JsonNode? node)
    {
        if (node is null) return "";
        if (node is JsonValue value)
        {
            try { return value.GetValue<string>(); } catch { }
            return value.ToJsonString();
        }
        return "";
    }

    private static IEnumerable<string> ParseTags(JsonNode? node)
    {
        if (node is JsonArray array)
            return array.Select(ScalarText).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
        return ScalarText(node)
            .Split([',', '，', ';', '；', '\n', '\r'],
                StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }

    private static string FindImageById(IEnumerable<string> names, string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "";
        var direct = names.FirstOrDefault(x =>
            string.Equals(Path.GetFileNameWithoutExtension(x), id, StringComparison.OrdinalIgnoreCase));
        if (direct is not null) return direct;
        return names.FirstOrDefault(x =>
            Path.GetFileNameWithoutExtension(x).StartsWith(id, StringComparison.OrdinalIgnoreCase)) ?? "";
    }
}

public static class StickerManifestSynchronizer
{
    public static bool TryUpdate(string original,
        IReadOnlyList<StickerRow> rows,
        out string updated,
        out string error)
    {
        error = "";
        updated = original;
        var lines = original.Replace("\r\n", "\n").Split('\n').ToList();

        for (var headerIndex = 0; headerIndex < lines.Count; headerIndex++)
        {
            if (!lines[headerIndex].Contains('|', StringComparison.Ordinal)) continue;
            var headers = SplitCells(lines[headerIndex]);
            var tagIndex = FindColumn(headers, ["tag", "tags", "label", "labels", "标签"]);
            var idIndex = FindColumn(headers, ["id", "number", "no", "编号", "序号"]);
            var fileIndex = FindColumn(headers, ["file", "filename", "image", "path", "文件", "图片"]);
            if (tagIndex < 0 || (idIndex < 0 && fileIndex < 0) || headerIndex + 1 >= lines.Count) continue;
            if (!IsSeparator(lines[headerIndex + 1])) continue;

            var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = headerIndex + 2; i < lines.Count && lines[i].Contains('|', StringComparison.Ordinal); i++)
            {
                var cells = SplitCells(lines[i]);
                var row = MatchRow(rows, cells, idIndex, fileIndex);
                if (row is null || tagIndex >= cells.Count) continue;
                cells[tagIndex] = string.Join(", ", NormalizeTags(row.TagsText)).Replace("|", "\\|", StringComparison.Ordinal);
                lines[i] = JoinCells(cells, lines[i].StartsWith('|'));
                matched.Add(row.Id + "\0" + row.ImagePath);
            }

            if (matched.Count != rows.Count)
            {
                error = "MANIFEST.md 的表格未能与 catalog.json 中的所有贴图一一对应。标签未保存。";
                return false;
            }

            updated = string.Join("\n", lines).Replace("\n", Environment.NewLine);
            return true;
        }

        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var row = rows.FirstOrDefault(item =>
                (!string.IsNullOrWhiteSpace(item.ImagePath) &&
                 line.Contains(Path.GetFileName(item.ImagePath), StringComparison.OrdinalIgnoreCase)) ||
                Regex.IsMatch(line, $@"(?<!\d){Regex.Escape(item.Id)}(?!\d)"));
            if (row is null) continue;

            var match = Regex.Match(line, @"(?<prefix>(?:tags?|labels?|标签)\s*[:：]\s*)(?<value>.*)$",
                RegexOptions.IgnoreCase);
            if (!match.Success) continue;
            var tags = string.Join(", ", NormalizeTags(row.TagsText)).Replace("|", "\\|", StringComparison.Ordinal);
            lines[i] = line[..match.Groups["value"].Index] + tags;
            changed.Add(row.Id + "\0" + row.ImagePath);
        }

        if (changed.Count != rows.Count)
        {
            error = "无法安全识别 MANIFEST.md 中每张贴图的标签字段。请先检查文件格式；没有写入任何文件。";
            return false;
        }

        updated = string.Join("\n", lines).Replace("\n", Environment.NewLine);
        return true;
    }

    private static StickerRow? MatchRow(IReadOnlyList<StickerRow> rows,
        IReadOnlyList<string> cells, int idIndex, int fileIndex)
    {
        foreach (var row in rows)
        {
            var idMatches = idIndex >= 0 && idIndex < cells.Count &&
                string.Equals(CleanCell(cells[idIndex]), row.Id, StringComparison.OrdinalIgnoreCase);
            var fileMatches = fileIndex >= 0 && fileIndex < cells.Count &&
                string.Equals(Path.GetFileName(CleanCell(cells[fileIndex])),
                    Path.GetFileName(row.ImagePath), StringComparison.OrdinalIgnoreCase);
            if (idMatches || fileMatches) return row;
        }
        return null;
    }

    private static List<string> SplitCells(string line) =>
        line.Trim().Trim('|').Split('|').Select(x => x.Trim()).ToList();

    private static string JoinCells(IReadOnlyList<string> cells, bool leadingPipe) =>
        (leadingPipe ? "| " : "") + string.Join(" | ", cells) + (leadingPipe ? " |" : "");

    private static bool IsSeparator(string line) =>
        Regex.IsMatch(line.Trim(), @"^\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)+\|?$");

    private static int FindColumn(IReadOnlyList<string> headers, IEnumerable<string> names)
    {
        for (var i = 0; i < headers.Count; i++)
            if (names.Any(name => string.Equals(CleanCell(headers[i]), name, StringComparison.OrdinalIgnoreCase)))
                return i;
        return -1;
    }

    private static string CleanCell(string value) =>
        Regex.Replace(value.Trim(), @"[\u0060*_]", "");

    private static IEnumerable<string> NormalizeTags(string value) =>
        value.Split([',', '，', ';', '；', '\n', '\r'],
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase);
}