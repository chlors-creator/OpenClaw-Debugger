using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

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
