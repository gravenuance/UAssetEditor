using CommunityToolkit.Mvvm.ComponentModel;
using UAssetEditor.Core.Search;

namespace UAssetEditor.App.ViewModels;

/// <summary>
/// One chip in a <see cref="Controls.TermsBox"/> - wraps a <see cref="ConditionTerm"/> with
/// a mutable, observable <see cref="Tag"/> so clicking a chip's AND/OR/NOT badge updates its
/// color/label immediately (see <see cref="Controls.TermsBox.CycleTag_Click"/>) without
/// rebuilding the owning collection.
/// </summary>
public sealed partial class ConditionTermViewModel : ObservableObject
{
    public string Text { get; }

    [ObservableProperty] private TermTag _tag;

    public ConditionTermViewModel(string text, TermTag tag = TermTag.And)
    {
        Text = text;
        Tag = tag;
    }

    public ConditionTerm ToCore() => new(Text, Tag);
}
