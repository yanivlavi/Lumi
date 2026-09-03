using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using QRCoder;

namespace Lumi.Views.Controls;

public sealed class QrCodeControl : Control
{
    // QRCoder's module matrix already includes the standard four-module quiet zone.
    private const int QuietZoneModules = 0;
    private bool[,]? _modules;

    public static readonly StyledProperty<string?> ValueProperty =
        AvaloniaProperty.Register<QrCodeControl, string?>(nameof(Value));

    public string? Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    internal int ModuleCount => _modules?.GetLength(0) ?? 0;

    protected override Size MeasureOverride(Size availableSize)
    {
        const double defaultSize = 208;
        var width = double.IsInfinity(availableSize.Width) ? defaultSize : availableSize.Width;
        var height = double.IsInfinity(availableSize.Height) ? defaultSize : availableSize.Height;
        var size = Math.Min(width, height);
        return new Size(size, size);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != ValueProperty)
            return;

        _modules = CreateModules(change.NewValue as string);
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = new Rect(Bounds.Size);
        context.DrawRectangle(Brushes.White, null, bounds);

        var modules = _modules;
        if (modules is null)
            return;

        var count = modules.GetLength(0);
        var moduleSize = Math.Floor(
            Math.Min(bounds.Width, bounds.Height) /
            (count + QuietZoneModules * 2));
        if (moduleSize < 1)
            return;

        var renderedSize = moduleSize * (count + QuietZoneModules * 2);
        var startX = Math.Floor((bounds.Width - renderedSize) / 2)
            + QuietZoneModules * moduleSize;
        var startY = Math.Floor((bounds.Height - renderedSize) / 2)
            + QuietZoneModules * moduleSize;

        for (var row = 0; row < count; row++)
        {
            for (var column = 0; column < count; column++)
            {
                if (!modules[row, column])
                    continue;

                context.DrawRectangle(
                    Brushes.Black,
                    null,
                    new Rect(
                        startX + column * moduleSize,
                        startY + row * moduleSize,
                        moduleSize,
                        moduleSize));
            }
        }
    }

    internal static bool[,]? CreateModules(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        using var data = QRCodeGenerator.GenerateQrCode(
            value,
            QRCodeGenerator.ECCLevel.M);
        var count = data.ModuleMatrix.Count;
        var modules = new bool[count, count];
        for (var row = 0; row < count; row++)
        {
            for (var column = 0; column < count; column++)
                modules[row, column] = data.ModuleMatrix[row][column];
        }

        return modules;
    }
}
