using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BuzzGUI.Interfaces;

namespace PedalPatch
{
    public partial class GUI : UserControl, IMachineGUI
    {
        // ── Cached brushes ────────────────────────────────────────────────────
        static readonly SolidColorBrush BrushOff      = new(Color.FromRgb(0x33, 0x33, 0x33));
        static readonly SolidColorBrush BrushOffBorder = new(Color.FromRgb(0x50, 0x50, 0x50));
        static readonly SolidColorBrush BrushOn       = new(Color.FromRgb(0x15, 0x72, 0xE8));
        static readonly SolidColorBrush BrushOnBorder = new(Color.FromRgb(0x44, 0xAA, 0xFF));

        // ── State ─────────────────────────────────────────────────────────────
        CMachine machine;
        Button[,] cells;
        TextBlock[] inLabels;
        TextBlock[] outLabels;
        bool dragging;
        bool dragSetValue;

        // ─────────────────────────────────────────────────────────────────────
        public GUI() { InitializeComponent(); PreviewMouseUp += (_, _) => dragging = false; }

        // ─────────────────────────────────────────────────────────────────────
        //  IMachineGUI — requires both get and set
        // ─────────────────────────────────────────────────────────────────────
        IMachine _iMachine;
        public IMachine Machine
        {
            get => _iMachine;
            set
            {
                if (machine != null) machine.PropertyChanged -= OnMachinePropertyChanged;

                _iMachine = value;
                machine   = value?.ManagedMachine as CMachine;

                if (machine == null) return;

                machine.PropertyChanged += OnMachinePropertyChanged;
                BuildMatrix();
                PopulatePatchCombo();
                RefreshMatrix();
                FadeSlider.Value      = machine.FadeTimeMs;
                FadeLabel.Text        = machine.FadeTimeMs.ToString();
                ChkPreserve.IsChecked = machine.PreserveClipboard;
                UpdatePasteButtons();
                SubTitle.Text = $"  {CMachine.NumInputs}×{CMachine.NumOutputs}  ·  {CMachine.NumPatches} patches";
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        void OnMachinePropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            Dispatcher.BeginInvoke((Action)(() =>
            {
                if (machine == null) return;
                switch (e.PropertyName)
                {
                    case nameof(CMachine.Routing):
                        RefreshMatrix();
                        break;
                    case nameof(CMachine.CurrentPatch):
                        PatchCombo.SelectionChanged -= PatchCombo_SelectionChanged;
                        PatchCombo.SelectedIndex     = machine.CurrentPatch;
                        PatchCombo.SelectionChanged += PatchCombo_SelectionChanged;
                        RefreshMatrix();
                        break;
                    case nameof(CMachine.HasClipboard):
                    case nameof(CMachine.PreserveClipboard):
                        UpdatePasteButtons();
                        ChkPreserve.IsChecked = machine.PreserveClipboard;
                        break;
                    case nameof(CMachine.InputLabels):
                        for (int i = 0; i < CMachine.NumInputs; i++)
                            inLabels[i].Text = machine.InputLabels[i];
                        break;
                    case nameof(CMachine.OutputLabels):
                        for (int o = 0; o < CMachine.NumOutputs; o++)
                            outLabels[o].Text = machine.OutputLabels[o];
                        break;
                    case nameof(CMachine.FadeTimeMs):
                        FadeSlider.Value = machine.FadeTimeMs;
                        FadeLabel.Text   = machine.FadeTimeMs.ToString();
                        break;
                }
            }));
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Matrix construction
        // ─────────────────────────────────────────────────────────────────────
        void BuildMatrix()
        {
            var grid = MatrixGrid;
            grid.Children.Clear();
            grid.RowDefinitions.Clear();
            grid.ColumnDefinitions.Clear();

            int rows = CMachine.NumInputs  + 1;
            int cols = CMachine.NumOutputs + 1;
            for (int r = 0; r < rows; r++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int c = 0; c < cols; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Corner
            var corner = new TextBlock
            {
                Text = "In \\ Out", Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
                FontSize = 9, HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(4, 0, 4, 4)
            };
            Grid.SetRow(corner, 0); Grid.SetColumn(corner, 0);
            grid.Children.Add(corner);

            // Output labels
            outLabels = new TextBlock[CMachine.NumOutputs];
            for (int o = 0; o < CMachine.NumOutputs; o++)
            {
                int co = o;
                var tb = MakeAxisLabel(machine.OutputLabels[o], isOutput: true);
                tb.MouseLeftButtonDown += (s, e) => { if (e.ClickCount == 2) BeginRenameOutput(co); };
                Grid.SetRow(tb, 0); Grid.SetColumn(tb, o + 1);
                grid.Children.Add(tb);
                outLabels[o] = tb;
            }

            cells    = new Button[CMachine.NumInputs, CMachine.NumOutputs];
            inLabels = new TextBlock[CMachine.NumInputs];

            for (int i = 0; i < CMachine.NumInputs; i++)
            {
                int ci = i;
                var lbl = MakeAxisLabel(machine.InputLabels[i], isOutput: false);
                lbl.MouseLeftButtonDown += (s, e) => { if (e.ClickCount == 2) BeginRenameInput(ci); };
                Grid.SetRow(lbl, i + 1); Grid.SetColumn(lbl, 0);
                grid.Children.Add(lbl);
                inLabels[i] = lbl;

                for (int o = 0; o < CMachine.NumOutputs; o++)
                {
                    int co = o;
                    var btn = new Button
                    {
                        Style       = (Style)FindResource("CellStyle"),
                        Background  = BrushOff,
                        BorderBrush = BrushOffBorder,
                        Tag         = (ci, co),
                        ToolTip     = $"In {ci + 1}  →  Out {co + 1}",
                    };
                    btn.PreviewMouseLeftButtonDown  += Cell_LeftDown;
                    btn.PreviewMouseRightButtonDown += Cell_RightDown;
                    btn.MouseEnter                  += Cell_MouseEnter;
                    Grid.SetRow(btn, i + 1); Grid.SetColumn(btn, o + 1);
                    grid.Children.Add(btn);
                    cells[i, o] = btn;
                }
            }
        }

        TextBlock MakeAxisLabel(string text, bool isOutput) => new()
        {
            Text                = text,
            Foreground          = new SolidColorBrush(Color.FromRgb(0xBB, 0xBB, 0xBB)),
            HorizontalAlignment = isOutput ? HorizontalAlignment.Center : HorizontalAlignment.Right,
            VerticalAlignment   = isOutput ? VerticalAlignment.Bottom   : VerticalAlignment.Center,
            Margin              = isOutput ? new Thickness(4, 0, 4, 6)  : new Thickness(0, 2, 8, 2),
            FontSize            = 11,
            Cursor              = Cursors.IBeam,
            ToolTip             = "Double-click to rename"
        };

        // ─────────────────────────────────────────────────────────────────────
        void RefreshMatrix()
        {
            if (machine == null || cells == null) return;
            for (int i = 0; i < CMachine.NumInputs; i++)
            for (int o = 0; o < CMachine.NumOutputs; o++)
            {
                bool on = machine.GetConnection(i, o);
                cells[i, o].Background  = on ? BrushOn       : BrushOff;
                cells[i, o].BorderBrush = on ? BrushOnBorder : BrushOffBorder;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Mouse interaction
        // ─────────────────────────────────────────────────────────────────────
        void Cell_LeftDown(object sender, MouseButtonEventArgs e)
        {
            if (machine == null) return;
            var (i, o) = ((int, int))((Button)sender).Tag;
            dragSetValue = !machine.GetConnection(i, o);
            machine.SetConnection(i, o, dragSetValue);
            dragging = true;
            e.Handled = true;
        }

        void Cell_RightDown(object sender, MouseButtonEventArgs e)
        {
            if (machine == null) return;
            var (i, o) = ((int, int))((Button)sender).Tag;
            machine.SetConnection(i, o, false);
            dragSetValue = false;
            dragging = true;
            e.Handled = true;
        }

        void Cell_MouseEnter(object sender, MouseEventArgs e)
        {
            if (!dragging || machine == null) return;
            var (i, o) = ((int, int))((Button)sender).Tag;
            machine.SetConnection(i, o, dragSetValue);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Controls
        // ─────────────────────────────────────────────────────────────────────
        void PopulatePatchCombo()
        {
            PatchCombo.SelectionChanged -= PatchCombo_SelectionChanged;
            PatchCombo.Items.Clear();
            for (int p = 0; p < CMachine.NumPatches; p++) PatchCombo.Items.Add($"Patch {p + 1:D2}");
            PatchCombo.SelectedIndex = machine.CurrentPatch;
            PatchCombo.SelectionChanged += PatchCombo_SelectionChanged;
        }

        void UpdatePasteButtons()
        {
            bool has = machine?.HasClipboard ?? false;
            BtnPaste.IsEnabled = has;
            BtnMerge.IsEnabled = has;
        }

        void PatchCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (machine == null || PatchCombo.SelectedIndex < 0) return;
            machine.CurrentPatch = PatchCombo.SelectedIndex;
        }

        void Copy_Click(object sender, RoutedEventArgs e)      => machine?.CopyCurrentPatch();
        void Paste_Click(object sender, RoutedEventArgs e)     => machine?.PasteCurrentPatch(merge: false);
        void Merge_Click(object sender, RoutedEventArgs e)     => machine?.PasteCurrentPatch(merge: true);

        void Clear_Click(object sender, RoutedEventArgs e)
        {
            if (machine == null) return;
            if (machine.ConfirmOnClear &&
                MessageBox.Show("Clear current patch?", "Pedal Patcher",
                                MessageBoxButton.OKCancel, MessageBoxImage.Question)
                    != MessageBoxResult.OK) return;
            machine.ClearCurrentPatch();
        }

        void ClearAll_Click(object sender, RoutedEventArgs e)
        {
            if (machine == null) return;
            if (MessageBox.Show("Clear ALL patches?\nThis cannot be undone.", "Pedal Patcher",
                                MessageBoxButton.OKCancel, MessageBoxImage.Warning)
                    != MessageBoxResult.OK) return;
            machine.ClearAllPatches();
        }

        void ChkPreserve_Changed(object sender, RoutedEventArgs e)
        {
            if (machine == null) return;
            machine.PreserveClipboard = ChkPreserve.IsChecked == true;
        }

        void FadeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (machine == null) return;
            machine.FadeTimeMs = (int)e.NewValue;
            FadeLabel.Text     = machine.FadeTimeMs.ToString();
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Label renaming
        // ─────────────────────────────────────────────────────────────────────
        void BeginRenameInput(int index)
        {
            string result = PromptDialog.Ask($"Rename input {index + 1}:", machine.InputLabels[index]);
            if (result != null) machine.SetInputLabel(index, result);
        }

        void BeginRenameOutput(int index)
        {
            string result = PromptDialog.Ask($"Rename output {index + 1}:", machine.OutputLabels[index]);
            if (result != null) machine.SetOutputLabel(index, result);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    internal static class PromptDialog
    {
        public static string Ask(string prompt, string defaultValue = "")
        {
            var win = new Window
            {
                Title = "Pedal Patcher", Width = 320, Height = 120,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ResizeMode  = ResizeMode.NoResize,
                Background  = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22)),
                Foreground  = Brushes.White
            };
            var sp = new StackPanel { Margin = new Thickness(12) };
            sp.Children.Add(new TextBlock { Text = prompt, Foreground = Brushes.LightGray, Margin = new Thickness(0,0,0,6) });
            var tb = new TextBox
            {
                Text = defaultValue, Padding = new Thickness(4,2,4,2), Margin = new Thickness(0,0,0,8),
                Background = new SolidColorBrush(Color.FromRgb(0x33,0x33,0x33)),
                Foreground = Brushes.White, CaretBrush = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x55,0x55,0x55))
            };
            tb.SelectAll();
            sp.Children.Add(tb);
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var ok     = new Button { Content = "OK",     Width = 70, IsDefault = true,  Margin = new Thickness(0,0,4,0) };
            var cancel = new Button { Content = "Cancel", Width = 70, IsCancel  = true };
            row.Children.Add(ok); row.Children.Add(cancel);
            sp.Children.Add(row);
            win.Content = sp;
            string result = null;
            ok.Click     += (_, _) => { result = tb.Text; win.DialogResult = true; };
            cancel.Click += (_, _) => { win.DialogResult = false; };
            win.Loaded   += (_, _) => tb.Focus();
            win.ShowDialog();
            return result;
        }
    }
}
