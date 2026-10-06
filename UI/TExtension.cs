using System.Windows.Markup;
using ClipDeck.Core;

namespace ClipDeck.UI;

// {ui:T 'Türkçe metin'} in XAML; resolved once when the window loads, so a language change needs a restart.
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension : MarkupExtension
{
    public TExtension() { }
    public TExtension(string text) => Text = text;

    [ConstructorArgument("text")]
    public string Text { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.T(Text);
}
