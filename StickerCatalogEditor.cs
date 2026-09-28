using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenClawDebugger;

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
