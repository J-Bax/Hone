using System.Globalization;
using System.Reflection;

namespace Hone.Reporting.Dashboard;

/// <summary>
/// Generates a self-contained HTML performance dashboard by injecting data into
/// an embedded HTML template. Replaces <c>Export-Dashboard.ps1</c>.
/// </summary>
public static class DashboardExporter
{
    private const string TemplateResourceName = "Hone.Reporting.Dashboard.DashboardTemplate.html";

    /// <summary>
    /// Builds the complete HTML dashboard string by reading the embedded template
    /// and replacing all <c>__PLACEHOLDER__</c> tokens with values from <paramref name="data"/>.
    /// </summary>
    public static string Build(DashboardData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        string template = ReadEmbeddedTemplate();

        string generatedAt = (data.GeneratedAtUtc ?? DateTimeOffset.UtcNow)
            .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        string html = template
            .Replace("__DATA_JSON__", data.DataJson, StringComparison.Ordinal)
            .Replace("__COUNTER_JSON__", data.CounterJson, StringComparison.Ordinal)
            .Replace("__TIMESERIES_JSON__", data.TimeSeriesJson, StringComparison.Ordinal)
            .Replace("__RUN_METADATA_JSON__", data.RunMetadataJson, StringComparison.Ordinal)
            .Replace("__SCENARIO_JSON__", data.ScenarioJson, StringComparison.Ordinal)
            .Replace("__GENERATED_AT__", generatedAt, StringComparison.Ordinal)
            .Replace("__MIN_IMPROVE__", data.MinImprovePct.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("__MAX_REGRESS__", data.MaxRegressPct.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("__COUNTER_CHART_JSON__", data.CounterChartJson, StringComparison.Ordinal);

        return html;
    }

    private static string ReadEmbeddedTemplate()
    {
        Assembly assembly = typeof(DashboardExporter).Assembly;

        using Stream? stream = assembly.GetManifestResourceStream(TemplateResourceName);

        if (stream is null)
        {
            string[] names = assembly.GetManifestResourceNames();
            throw new InvalidOperationException(
                $"Embedded resource '{TemplateResourceName}' not found. " +
                $"Available resources: {string.Join(", ", names)}");
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
