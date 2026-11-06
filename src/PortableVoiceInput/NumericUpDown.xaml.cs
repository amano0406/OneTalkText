using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PortableVoiceInput;

public partial class NumericUpDown : System.Windows.Controls.UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(NumericUpDown), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public double Minimum { get; set; } = 0;
    public double Maximum { get; set; } = 100;
    public double Step { get; set; } = 1;

    public NumericUpDown() => InitializeComponent();

    private bool CanInsert(string text)
    {
        var proposed = InputBox.Text.Remove(InputBox.SelectionStart, InputBox.SelectionLength).Insert(InputBox.SelectionStart, text);
        return Regex.IsMatch(proposed, Minimum < 0 ? @"^-?[0-9]*(\.[0-9]*)?$" : @"^[0-9]*(\.[0-9]*)?$");
    }

    private void InputBox_PreviewTextInput(object sender, TextCompositionEventArgs e) => e.Handled = !CanInsert(e.Text);
    private void InputBox_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (!e.DataObject.GetDataPresent(DataFormats.UnicodeText) || e.DataObject.GetData(DataFormats.UnicodeText) is not string text || !CanInsert(text))
            e.CancelCommand();
    }
    private void InputBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key is not (Key.Up or Key.Down)) return;
        Change(e.Key == Key.Up ? 1 : -1); e.Handled = true;
    }
    private void Increase_Click(object sender, RoutedEventArgs e) => Change(1);
    private void Decrease_Click(object sender, RoutedEventArgs e) => Change(-1);
    private void Change(int direction)
    {
        if (!double.TryParse(Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
            value = Minimum;
        value = Math.Clamp(Math.Round(value + direction * Step, 8), Minimum, Maximum);
        Text = value.ToString("0.########", CultureInfo.InvariantCulture);
    }
}
