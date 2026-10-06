using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace ClipDeck.UI;

// Language-agnostic colouring for previews: enough to make a snippet of code recognisable at a glance.
public static partial class CodeText
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.RegisterAttached(
        "Source", typeof(string), typeof(CodeText), new PropertyMetadata(null, OnSourceChanged));

    public static string? GetSource(DependencyObject d) => (string?)d.GetValue(SourceProperty);
    public static void SetSource(DependencyObject d, string? value) => d.SetValue(SourceProperty, value);

    static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb) return;
        tb.Inlines.Clear();
        if (e.NewValue is not string text || text.Length == 0) return;

        int pos = 0;
        foreach (Match m in Token().Matches(text))
        {
            if (m.Index > pos) tb.Inlines.Add(new Run(text[pos..m.Index]));
            var run = new Run(m.Value);
            string key = m.Groups["c"].Success ? "CodeComment"
                : m.Groups["s"].Success ? "CodeString"
                : m.Groups["n"].Success ? "CodeNumber"
                : m.Groups["k"].Success ? "CodeKeyword"
                : "CodeTag";
            run.SetResourceReference(TextElement.ForegroundProperty, key);
            tb.Inlines.Add(run);
            pos = m.Index + m.Length;
        }
        if (pos < text.Length) tb.Inlines.Add(new Run(text[pos..]));
    }

    [GeneratedRegex("""
        (?<c>//[^\n]*|/\*[\s\S]*?\*/|<!--[\s\S]*?-->|(?<=^|\s)\#[^\n]*|(?<=^|\s)--\s[^\n]*)
        |(?<s>"(?:\\.|[^"\\\n])*"|'(?:\\.|[^'\\\n])*'|`[^`]*`)
        |(?<n>\b\d+(?:\.\d+)?\b)
        |(?<k>\b(?:abstract|and|as|async|await|bool|break|case|catch|char|class|const|continue|def|default|do|double|elif|else|enum|except|export|extends|false|False|final|finally|float|fn|for|foreach|from|func|function|if|impl|import|in|int|interface|is|lambda|let|long|match|mut|namespace|new|nil|None|not|null|or|override|package|pass|private|protected|pub|public|raise|readonly|return|self|static|string|struct|super|switch|this|throw|true|True|try|type|typeof|use|using|var|void|while|with|yield|SELECT|FROM|WHERE|INSERT|INTO|UPDATE|DELETE|JOIN|LEFT|INNER|ON|AND|OR|NOT|NULL|ORDER|BY|GROUP|LIMIT|VALUES|SET|CREATE|TABLE)\b)
        |(?<t></?[A-Za-z][\w:-]*|/?>)
        """, RegexOptions.IgnorePatternWhitespace | RegexOptions.Multiline)]
    private static partial Regex Token();
}
