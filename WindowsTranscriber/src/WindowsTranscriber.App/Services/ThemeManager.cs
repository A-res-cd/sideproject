using Microsoft.Win32;
using System.Windows;
using System.Windows.Media;
using WindowsTranscriber.Core.Models;

namespace WindowsTranscriber.App.Services;

public static class ThemeManager
{
    private static bool? _lastAppliedDarkTheme;

    public static void Apply(AppThemeMode mode)
    {
        var application = System.Windows.Application.Current;
        if (application is null)
        {
            return;
        }

        var useDarkTheme = mode == AppThemeMode.Dark ||
            mode == AppThemeMode.System && IsSystemDarkTheme();
        if (_lastAppliedDarkTheme == useDarkTheme)
        {
            return;
        }

        var colors = useDarkTheme
            ? DarkColors
            : LightColors;

        foreach (var (key, color) in colors)
        {
            var brush = new SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter
                    .ConvertFromString(color));
            brush.Freeze();
            application.Resources[key] = brush;
        }

        _lastAppliedDarkTheme = useDarkTheme;
    }

    private static bool IsSystemDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch
        {
            return false;
        }
    }

    private static readonly IReadOnlyDictionary<string, string> LightColors =
        new Dictionary<string, string>
        {
            ["AppBackgroundBrush"] = "#F4F6FA",
            ["SurfaceBrush"] = "#FFFFFF",
            ["SurfaceAltBrush"] = "#F8FAFC",
            ["BorderBrush"] = "#E2E8F0",
            ["StrongBorderBrush"] = "#CBD5E1",
            ["PrimaryTextBrush"] = "#0F172A",
            ["BodyTextBrush"] = "#334155",
            ["SecondaryTextBrush"] = "#64748B",
            ["MutedTextBrush"] = "#94A3B8",
            ["InputBrush"] = "#FFFFFF",
        };

    private static readonly IReadOnlyDictionary<string, string> DarkColors =
        new Dictionary<string, string>
        {
            ["AppBackgroundBrush"] = "#0F172A",
            ["SurfaceBrush"] = "#111827",
            ["SurfaceAltBrush"] = "#1E293B",
            ["BorderBrush"] = "#334155",
            ["StrongBorderBrush"] = "#475569",
            ["PrimaryTextBrush"] = "#F8FAFC",
            ["BodyTextBrush"] = "#E2E8F0",
            ["SecondaryTextBrush"] = "#CBD5E1",
            ["MutedTextBrush"] = "#94A3B8",
            ["InputBrush"] = "#1E293B",
        };
}
