using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OpenClawDebugger;

public sealed class ParsedStickerCatalog
{
    internal sealed record Binding(
        StickerRow Row,
        JsonObject Item,
        string? IdProperty,
        string? ImageProperty,
        string? TagProperty,
        string? WeightProperty);

    private readonly JsonNode _root;
    private readonly JsonArray _entries;
    private readonly List<Binding> _bindings;
    private readonly JsonSerializerOptions _options = new() { WriteIndented = true };

    internal ParsedStickerCatalog(JsonNode root, JsonArray entries, List<Binding> bindings)
    {
        _root = root;
        _entries = entries;
        _bindings = bindings;
    }

    public IReadOnlyList<StickerRow> Rows => _bindings.Select(x => x.Row).ToArray();

    public bool TryAddImage(string fileName, out StickerRow row, out string error)
    {
        row = new StickerRow();
        error = "";
        var template = _bindings.LastOrDefault();
        var item = template is null ? new JsonObject() : (JsonObject)template.Item.DeepClone();
        var idProperty = template?.IdProperty ?? (template is null ? "id" : null);
        var imageProperty = template?.ImageProperty ?? (template is null ? "file" : null);
        var tagProperty = template?.TagProperty ?? (template is null ? "tags" : null);
        var weightProperty = template?.WeightProperty ?? "weight";

        if (idProperty is null && imageProperty is null)
        {
            error = "目录条目没有可安全识别的图片路径或编号字段，无法自动登记。";
            return false;
        }

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var id = stem;
        var idTemplate = idProperty is null ? null : item[idProperty];
        var numericIds = idProperty is not null && imageProperty is not null &&
            idTemplate is JsonValue numeric && numeric.TryGetValue<long>(out _);
        if (numericIds)
        {
            id = (_bindings.Select(x => long.TryParse(x.Row.Id, out var number) ? number : 0)
                .DefaultIfEmpty(0).Max() + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        id = MakeUniqueId(id);
        if (idProperty is not null) item[idProperty] = CreateIdNode(idTemplate, id);
        if (imageProperty is not null) item[imageProperty] = fileName;
        if (tagProperty is null)
        {
            error = "目录条目没有可识别的标签字段，无法自动登记。";
            return false;
        }
        item[tagProperty] = item[tagProperty] is JsonArray ? new JsonArray() : JsonValue.Create("");
        item[weightProperty] = JsonValue.Create(1d);

        _entries.Add(item);
        row = new StickerRow
        {
            Id = idProperty is null ? stem : ScalarText(item[idProperty]),
            ImagePath = fileName,
            TagsText = "",
            Weight = 1
        };
        _bindings.Add(new Binding(row, item, idProperty, imageProperty, tagProperty, weightProperty));
        return true;
    }

    public bool TryRenameImage(string oldPath, string newFileName, out StickerRow renamed, out string error)
    {
        renamed = new StickerRow();
        error = "";
        var index = _bindings.FindIndex(x => x.Row.ImagePath.Equals(oldPath, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            error = "此图片没有可编辑的目录条目。";
            return false;
        }
        if (_bindings.Where((binding, i) => i != index)
            .Any(binding => binding.Row.ImagePath.Equals(newFileName, StringComparison.OrdinalIgnoreCase)))
        {
            error = "目录中已经有同名图片。";
            return false;
        }

        var binding = _bindings[index];
        if (binding.ImageProperty is null && binding.IdProperty is null)
        {
            error = "目录条目没有可安全更新的图片路径或编号字段。";
            return false;
        }

        var newId = binding.ImageProperty is null
            ? Path.GetFileNameWithoutExtension(newFileName)
            : binding.Row.Id;
        if (binding.ImageProperty is not null) binding.Item[binding.ImageProperty] = newFileName;
        if (binding.ImageProperty is null && binding.IdProperty is not null)
            binding.Item[binding.IdProperty] = CreateIdNode(binding.Item[binding.IdProperty], newId);

        renamed = new StickerRow { Id = newId, ImagePath = newFileName, TagsText = binding.Row.TagsText, Weight = binding.Row.Weight };
        _bindings[index] = binding with { Row = renamed };
        return true;
    }

    public string SerializeWithEdits()
    {
        foreach (var binding in _bindings)
        {
            var row = binding.Row;
            var item = binding.Item;
            if (binding.IdProperty is not null)
                item[binding.IdProperty] = CreateIdNode(item[binding.IdProperty], row.Id);
            if (binding.ImageProperty is not null) item[binding.ImageProperty] = row.ImagePath;
            item[binding.WeightProperty ?? "weight"] = JsonValue.Create(row.Weight);

            var key = binding.TagProperty ?? "tags";
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

    private string MakeUniqueId(string preferred)
    {
        var existing = new HashSet<string>(_bindings.Select(x => x.Row.Id), StringComparer.OrdinalIgnoreCase);
        if (!existing.Contains(preferred)) return preferred;
        var suffix = 2;
        while (existing.Contains(preferred + "-" + suffix)) suffix++;
        return preferred + "-" + suffix;
    }

    private static JsonNode? CreateIdNode(JsonNode? template, string id)
    {
        if (template is JsonValue value && value.TryGetValue<long>(out _) &&
            long.TryParse(id, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var number))
            return JsonValue.Create(number);
        return JsonValue.Create(id);
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
}

public static class StickerCatalogEditor
{
    private static readonly string[] ArrayNames = ["stickers", "items", "entries", "images", "catalog"];
    private static readonly string[] IdNames = ["id", "number", "index", "no", "编号"];
    private static readonly string[] ImageNames = ["file", "filename", "image", "path", "src", "asset", "name"];
    private static readonly string[] TagNames = ["tags", "labels", "keywords", "标签"];
    private static readonly string[] WeightNames = ["weight", "selectionWeight", "selection_weight", "权重"];

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

        var bindings = new List<ParsedStickerCatalog.Binding>();
        if (array.Count == 0)
        {
            catalog = new ParsedStickerCatalog(root, array, bindings);
            return true;
        }

        var imageNames = images.Where(x => x.IsImage).Select(x => x.RelativePath).ToArray();
        foreach (var node in array)
        {
            if (node is not JsonObject item) continue;
            var idProp = FindProperty(item, IdNames);
            var imageProp = FindProperty(item, ImageNames);
            var tagProp = FindProperty(item, TagNames);
            var weightProp = FindProperty(item, WeightNames);
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
            var weight = 1d;
            if (weightProp is not null && item[weightProp] is not null)
            {
                if (item[weightProp] is not JsonValue weightValue || !weightValue.TryGetValue<double>(out weight) ||
                    !double.IsFinite(weight) || weight < 0 || weight > 1_000_000)
                {
                    error = "表情包权重必须是 0 到 1,000,000 之间的有限数字。";
                    return false;
                }
            }
            var imagePath = imageProp is null ? "" : ScalarText(item[imageProp]);
            if (string.IsNullOrWhiteSpace(imagePath))
                imagePath = FindImageById(imageNames, id);
            if (string.IsNullOrWhiteSpace(id))
                id = Path.GetFileNameWithoutExtension(imagePath);
            if (string.IsNullOrWhiteSpace(id) && string.IsNullOrWhiteSpace(imagePath))
                continue;

            var tags = ParseTags(item[tagProp]);
            var row = new StickerRow
            {
                Id = id,
                ImagePath = imagePath,
                TagsText = string.Join(", ", tags),
                Weight = weight
            };
            bindings.Add(new ParsedStickerCatalog.Binding(row, item, idProp, imageProp, tagProp, weightProp));
        }

        if (bindings.Count == 0)
        {
            error = "catalog.json 中没有识别到表情包条目。";
            return false;
        }

        catalog = new ParsedStickerCatalog(root, array, bindings);
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
            var weightIndex = FindColumn(headers, ["weight", "selectionweight", "selection_weight", "权重"]);
            var idIndex = FindColumn(headers, ["id", "number", "no", "编号", "序号"]);
            var fileIndex = FindColumn(headers, ["file", "filename", "image", "path", "文件", "图片"]);
            if (tagIndex < 0 || (idIndex < 0 && fileIndex < 0) || headerIndex + 1 >= lines.Count) continue;
            if (!IsSeparator(lines[headerIndex + 1])) continue;

            if (weightIndex < 0)
            {
                headers.Add("权重");
                lines[headerIndex] = JoinCells(headers, lines[headerIndex].StartsWith('|'));
                var separator = SplitCells(lines[headerIndex + 1]);
                separator.Add("---");
                lines[headerIndex + 1] = JoinCells(separator, lines[headerIndex + 1].StartsWith('|'));
                weightIndex = headers.Count - 1;
            }

            var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = headerIndex + 2; i < lines.Count && lines[i].Contains('|', StringComparison.Ordinal); i++)
            {
                var cells = SplitCells(lines[i]);
                var row = MatchRow(rows, cells, idIndex, fileIndex);
                if (row is null) continue;
                while (cells.Count < headers.Count) cells.Add("");
                cells[tagIndex] = string.Join(", ", NormalizeTags(row.TagsText)).Replace("|", "\\|", StringComparison.Ordinal);
                cells[weightIndex] = FormatWeight(row.Weight);
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

    public static bool TryAppendRow(string original, IReadOnlyList<StickerRow> existingRows,
        StickerRow newRow, out string updated, out string error)
    {
        updated = original;
        error = "";
        var lines = original.Replace("\r\n", "\n").Split('\n').ToList();

        for (var headerIndex = 0; headerIndex < lines.Count; headerIndex++)
        {
            if (!lines[headerIndex].Contains('|', StringComparison.Ordinal)) continue;
            var headers = SplitCells(lines[headerIndex]);
            var tagIndex = FindColumn(headers, ["tag", "tags", "label", "labels", "标签"]);
            var idIndex = FindColumn(headers, ["id", "number", "no", "编号", "序号"]);
            var fileIndex = FindColumn(headers, ["file", "filename", "image", "path", "文件", "图片"]);
            if (tagIndex < 0 || (idIndex < 0 && fileIndex < 0) ||
                headerIndex + 1 >= lines.Count || !IsSeparator(lines[headerIndex + 1])) continue;

            var endIndex = headerIndex + 2;
            var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (endIndex < lines.Count && lines[endIndex].Contains('|', StringComparison.Ordinal))
            {
                var cells = SplitCells(lines[endIndex]);
                var match = MatchRow(existingRows, cells, idIndex, fileIndex);
                if (match is not null) matched.Add(match.Id + "\0" + match.ImagePath);
                endIndex++;
            }
            if (matched.Count != existingRows.Count) continue;

            var newCells = Enumerable.Repeat("", headers.Count).ToList();
            if (idIndex >= 0 && idIndex < newCells.Count) newCells[idIndex] = newRow.Id;
            if (fileIndex >= 0 && fileIndex < newCells.Count) newCells[fileIndex] = Path.GetFileName(newRow.ImagePath);
            if (tagIndex >= 0 && tagIndex < newCells.Count) newCells[tagIndex] = "";
            lines.Insert(endIndex, JoinCells(newCells, lines[headerIndex].TrimStart().StartsWith('|')));
            var candidate = string.Join("\n", lines).Replace("\n", Environment.NewLine);
            return TryUpdate(candidate, existingRows.Append(newRow).ToArray(), out updated, out error);
        }

        var trimmed = original.TrimEnd('\r', '\n');
        var newLine = newRow.Id + " " + Path.GetFileName(newRow.ImagePath) + " 标签：";
        var candidateText = string.IsNullOrEmpty(trimmed)
            ? newLine
            : trimmed + Environment.NewLine + newLine;
        return TryUpdate(candidateText, existingRows.Append(newRow).ToArray(), out updated, out error);
    }

    public static bool TryRenameReferences(string original, StickerRow oldRow, StickerRow newRow,
        IReadOnlyList<StickerRow> rows, out string updated, out string error)
    {
        updated = original;
        error = "";
        var lines = original.Replace("\r\n", "\n").Split('\n').ToList();

        for (var headerIndex = 0; headerIndex < lines.Count; headerIndex++)
        {
            if (!lines[headerIndex].Contains('|', StringComparison.Ordinal)) continue;
            var headers = SplitCells(lines[headerIndex]);
            var tagIndex = FindColumn(headers, ["tag", "tags", "label", "labels", "标签"]);
            var idIndex = FindColumn(headers, ["id", "number", "no", "编号", "序号"]);
            var fileIndex = FindColumn(headers, ["file", "filename", "image", "path", "文件", "图片"]);
            if (tagIndex < 0 || (idIndex < 0 && fileIndex < 0) ||
                headerIndex + 1 >= lines.Count || !IsSeparator(lines[headerIndex + 1])) continue;

            var found = 0;
            for (var i = headerIndex + 2; i < lines.Count && lines[i].Contains('|', StringComparison.Ordinal); i++)
            {
                var cells = SplitCells(lines[i]);
                if (MatchRow([oldRow], cells, idIndex, fileIndex) is null) continue;
                found++;
                if (idIndex >= 0 && idIndex < cells.Count) cells[idIndex] = newRow.Id;
                if (fileIndex >= 0 && fileIndex < cells.Count) cells[fileIndex] = Path.GetFileName(newRow.ImagePath);
                lines[i] = JoinCells(cells, lines[i].StartsWith('|'));
            }
            if (found != 1)
            {
                error = "MANIFEST.md 中没有唯一匹配的图片记录，不能安全重命名。";
                return false;
            }

            var candidate = string.Join("\n", lines).Replace("\n", Environment.NewLine);
            return TryUpdate(candidate, rows, out updated, out error);
        }

        var oldFile = Path.GetFileName(oldRow.ImagePath);
        var newFile = Path.GetFileName(newRow.ImagePath);
        var matchedLines = new List<int>();
        for (var i = 0; i < lines.Count; i++)
        {
            var fileMatch = lines[i].Contains(oldFile, StringComparison.OrdinalIgnoreCase);
            var idMatch = !string.IsNullOrWhiteSpace(oldRow.Id) &&
                Regex.IsMatch(lines[i], $@"(?<!\d){Regex.Escape(oldRow.Id)}(?!\d)");
            if (fileMatch || idMatch) matchedLines.Add(i);
        }
        if (matchedLines.Count != 1)
        {
            error = "MANIFEST.md 中没有唯一匹配的图片记录，不能安全重命名。";
            return false;
        }
        var line = lines[matchedLines[0]].Replace(oldFile, newFile, StringComparison.OrdinalIgnoreCase);
        if (!oldRow.Id.Equals(newRow.Id, StringComparison.Ordinal))
            line = Regex.Replace(line, $@"(?<!\d){Regex.Escape(oldRow.Id)}(?!\d)", newRow.Id);
        lines[matchedLines[0]] = line;
        var fallback = string.Join("\n", lines).Replace("\n", Environment.NewLine);
        return TryUpdate(fallback, rows, out updated, out error);
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

    private static string FormatWeight(double value) =>
        value.ToString("0.################", System.Globalization.CultureInfo.InvariantCulture);

    private static IEnumerable<string> NormalizeTags(string value) =>
        value.Split([',', '，', ';', '；', '\n', '\r'],
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase);
}
