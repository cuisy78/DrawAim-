using System.Windows;
using System.Windows.Controls;

namespace DrawAim.App;

public partial class MainWindow
{
    private void FitInitialWindowToWorkArea()
    {
        if (WindowState != WindowState.Normal)
        {
            return;
        }

        Rect workArea = SystemParameters.WorkArea;
        if (workArea.Width > 0 && double.IsFinite(workArea.Width))
        {
            Width = Math.Max(MinWidth, Math.Min(Width, workArea.Width - 8));
        }

        if (workArea.Height > 0 && double.IsFinite(workArea.Height))
        {
            Height = Math.Max(MinHeight, Math.Min(Height, workArea.Height - 8));
        }
    }

    private void ApplyResponsiveLayout()
    {
        double width = ActualWidth > 0 ? ActualWidth : Width;
        double height = ActualHeight > 0 ? ActualHeight : Height;
        bool compact = width < 1_200;
        bool shortWindow = height < 650;

        NavigationColumn.Width = new GridLength(compact ? 170 : 210);
        Mode1SettingsColumn.Width = new GridLength(compact ? 180 : 250);
        Mode1ResultColumn.Width = new GridLength(compact ? 170 : 230);
        Mode2SettingsColumn.Width = new GridLength(compact ? 155 : 220);
        Mode2ResultColumn.Width = new GridLength(compact ? 155 : 220);
        Mode3PickerColumn.Width = new GridLength(compact ? 250 : 340);
        Mode3ResultColumn.Width = new GridLength(compact ? 190 : 245);

        double pageMargin = compact ? 10 : 22;
        double verticalMargin = shortWindow ? 7 : pageMargin;
        var trainingMargin = new Thickness(pageMargin, verticalMargin, pageMargin, verticalMargin);
        Mode1Page.Margin = trainingMargin;
        Mode2Page.Margin = trainingMargin;
        Mode3Page.Margin = trainingMargin;

        double footerSide = compact ? 169 : 234;
        Mode2Footer.Margin = new Thickness(
            footerSide,
            shortWindow ? 6 : 12,
            footerSide,
            0);
        Mode2FooterShortcut.Visibility = width < 1_050
            ? Visibility.Collapsed
            : Visibility.Visible;

        double pickerSide = !compact
            ? 290
            : shortWindow
                ? 190
                : 200;
        Mode3ColorPicker.Width = pickerSide;
        Mode3ColorPicker.Height = pickerSide;
        Mode3UndoButton.Content = compact ? "撤销" : "撤销  Ctrl+Z";
        Mode3RedoButton.Content = compact ? "重做" : "重做  Ctrl+Y";
        Mode3ClearButton.Content = compact ? "清空" : "清空  Delete";

        ResizeMode2Squares();
    }
}
