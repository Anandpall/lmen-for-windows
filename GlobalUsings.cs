// Disambiguate WPF types from WinForms types.
// These aliases win over the namespaces brought in by <ImplicitUsings>
// (System.Windows.Forms is auto-imported because UseWindowsForms=true).
// 'Button' matters: without it, "new Button()" inside a Window class binds to
// System.Windows.Forms.Button and fails to compile (CS0029/CS1503).
global using Button = System.Windows.Controls.Button;
global using Color = System.Windows.Media.Color;
global using Application = System.Windows.Application;
global using KeyEventArgs = System.Windows.Input.KeyEventArgs;
global using MessageBox = System.Windows.MessageBox;
global using Point = System.Windows.Point;
global using Brushes = System.Windows.Media.Brushes;
global using HorizontalAlignment = System.Windows.HorizontalAlignment;
global using Orientation = System.Windows.Controls.Orientation;
global using FontFamily = System.Windows.Media.FontFamily;
global using Size = System.Windows.Size;
global using Pen = System.Windows.Media.Pen;
