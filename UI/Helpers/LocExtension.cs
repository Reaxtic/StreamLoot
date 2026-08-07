using System.Windows.Markup;
using System.Windows.Data;
using Core.Managers;

namespace UI.Helpers
{
    /// <summary>
    /// XAML markup extension for localized strings: <c>Text="{loc:T Nav.Dashboard}"</c>.
    /// Returns a binding to <see cref="Loc"/>'s indexer, so switching the language in Settings updates every
    /// bound label live (Loc raises "Item[]" on change) without restarting the app.
    /// </summary>
    [MarkupExtensionReturnType(typeof(object))]
    public sealed class TExtension : MarkupExtension
    {
        public string Key { get; set; } = string.Empty;

        public TExtension() { }
        public TExtension(string key) => Key = key;

        public override object ProvideValue(IServiceProvider serviceProvider)
        {
            System.Windows.Data.Binding binding = new System.Windows.Data.Binding($"[{Key}]")
            {
                Source = Loc.Instance,
                Mode = BindingMode.OneWay
            };
            return binding.ProvideValue(serviceProvider);
        }
    }
}
