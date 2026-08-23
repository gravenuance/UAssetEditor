using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using UAssetEditor.App.ViewModels;
using UAssetEditor.Core.Search;

namespace UAssetEditor.App.Controls;

/// <summary>
/// A compact multi-value text filter: the closed box shows a clipped row of chip previews
/// (or "Any" when empty); clicking it opens a popup with every term as a removable, taggable
/// chip plus a field to add more. Each chip's AND/OR/NOT badge cycles on click (see
/// <see cref="CycleTag_Click"/>) - AND terms must all match, at least one OR term must match
/// if any are present, and NOT terms are a hard exclusion regardless of the AND/OR result
/// (see <see cref="ConditionMatcher.Matches"/> for the exact rule). <see cref="Terms"/> is
/// mutated directly (Add/Remove), the same direct-collection-mutation pattern the app already
/// uses for recent sources and rules.
/// </summary>
public partial class TermsBox : UserControl
{
    public static readonly DependencyProperty TermsProperty =
        DependencyProperty.Register(nameof(Terms), typeof(ObservableCollection<ConditionTermViewModel>), typeof(TermsBox));

    public ObservableCollection<ConditionTermViewModel> Terms
    {
        get => (ObservableCollection<ConditionTermViewModel>)GetValue(TermsProperty);
        set => SetValue(TermsProperty, value);
    }

    public TermsBox() => InitializeComponent();

    private void CycleTag_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not ConditionTermViewModel term) return;

        term.Tag = term.Tag switch
        {
            TermTag.And => TermTag.Or,
            TermTag.Or => TermTag.Not,
            _ => TermTag.And,
        };
    }

    private void RemoveTerm_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is ConditionTermViewModel term)
            Terms?.Remove(term);
    }

    private void NewTermBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        var text = NewTermBox.Text.Trim();
        if (text.Length > 0 && Terms != null && !Terms.Any(t => string.Equals(t.Text, text, System.StringComparison.OrdinalIgnoreCase)))
            Terms.Add(new ConditionTermViewModel(text));

        NewTermBox.Clear();
        e.Handled = true;
    }
}
