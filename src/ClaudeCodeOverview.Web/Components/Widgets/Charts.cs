using ApexCharts;

namespace ClaudeCodeOverview.Web.Components.Widgets;

/// <summary>Shared ApexCharts options so every chart follows the active MudBlazor theme.</summary>
public static class Charts
{
    public static ApexChartOptions<T> Base<T>(bool isDark, bool stacked = false, bool horizontal = false)
        where T : class
    {
        var options = new ApexChartOptions<T>
        {
            Chart = new Chart
            {
                Background = "transparent",
                Stacked = stacked,
                Toolbar = new Toolbar { Show = false },
                Animations = new Animations { Enabled = false },
            },
            Theme = new Theme { Mode = isDark ? Mode.Dark : Mode.Light },
        };
        if (horizontal)
        {
            options.PlotOptions = new PlotOptions { Bar = new PlotOptionsBar { Horizontal = true } };
        }
        return options;
    }
}
