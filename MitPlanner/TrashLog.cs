using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace MitPlanner;

/// <summary>
/// Plain-text run log for tuning Trash mode, one file per duty entry:
///   %AppData%\XIVLauncher\pluginConfigs\MitPlanner\trashlog\2026-11-02_2214_The Vault_WAR.log
/// Lines (tab-separated, easy to read and to grep):
///   time  EVENT  key=value ...
/// Events: ZONE, PULL (set), SNAP (once a second in combat), PRESS, SKIP (why something didn't
/// fire, rate-limited), EMERGENCY, INVULN, DEATH, END (pull summary).
/// </summary>
public sealed class TrashLog : IDisposable
{
    private readonly Configuration config;
    private StreamWriter? writer;
    private string? path;

    public string Folder { get; } = Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "trashlog");
    public string? CurrentFile => path;

    public TrashLog(Configuration config) => this.config = config;

    /// <summary>Start a new file (on entering a duty).</summary>
    public void Open(string zone, string job, string party)
    {
        Close();
        if (!config.Trash.Log) return;
        try
        {
            Directory.CreateDirectory(Folder);
            var safe = string.Concat(zone.Split(Path.GetInvalidFileNameChars()));
            path = Path.Combine(Folder, $"{DateTime.Now:yyyy-MM-dd_HHmm}_{safe}_{job}.log");
            writer = new StreamWriter(path, append: true, Encoding.UTF8) { AutoFlush = true };
            Write("ZONE", $"zone={zone}\tjob={job}\tparty={(party.Length > 0 ? party : "solo/trust?")}\tplugin={typeof(TrashLog).Assembly.GetName().Version}");
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Couldn't open the trash log");
            writer = null;
        }
    }

    public void Write(string evt, string details)
    {
        if (writer == null || !config.Trash.Log) return;
        try
        {
            writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{DateTime.Now:HH:mm:ss.f}\t{evt}\t{details}"));
        }
        catch (Exception) { /* never break combat over logging */ }
    }

    public void Close()
    {
        try { writer?.Dispose(); } catch (Exception) { }
        writer = null;
        path = null;
    }

    public void Dispose() => Close();
}
