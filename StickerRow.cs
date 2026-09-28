using System.ComponentModel;

namespace OpenClawDebugger;

public sealed class StickerRow : INotifyPropertyChanged
{
    private string _tagsText = "";
    private double _weight = 1;

    public string Id { get; init; } = "";
    public string ImagePath { get; init; } = "";
    public string TagsText
    {
        get => _tagsText;
        set
        {
            if (_tagsText == value) return;
            _tagsText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TagsText)));
        }
    }

    public double Weight
    {
        get => _weight;
        set
        {
            if (_weight.Equals(value)) return;
            _weight = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Weight)));
        }
    }

    public string TagSummary => string.IsNullOrWhiteSpace(TagsText) ? "无标签" : TagsText;
    public event PropertyChangedEventHandler? PropertyChanged;
    public override string ToString() => $"{Id} · {ImagePath}";
}
